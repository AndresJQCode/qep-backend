using FluentValidation;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>Lo del encabezado que comparten el guardado de una vez y su cálculo previo. Existe
/// para que la regla se escriba una sola vez — ver <see cref="QuotationHeaderRules"/>.</summary>
public interface IQuotationHeaderEdits
{
    string? PaymentMethod { get; }
}

/// <summary>
/// El estado deseado completo de una cotización editable: mismo cuerpo para
/// <c>PUT /quotations/{quotationId}</c> y <c>POST /quotations/{quotationId}/preview</c>, así que
/// las reglas y los pasos se escriben una vez. Sin esto el cálculo previo podría aceptar un
/// borrador que el guardado después rechaza, que es justo lo que la pantalla no puede manejar.
///
/// <c>Items</c> es la lista **completa** deseada, no un delta: el backend calcula la diferencia
/// contra lo guardado (<see cref="QuotationItemEdits"/>), igual que «Editar pedido».
/// </summary>
public interface IQuotationEdits : IQuotationHeaderEdits
{
    DateOnly? ValidUntil { get; }

    string? Notes { get; }

    QuotationPartiesRequest? Parties { get; }

    QuotationBillingAccountRequest? BillingAccount { get; }

    IReadOnlyList<QuotationItemAddition> Items { get; }

    /// <summary>
    /// El piso de escala global, o <c>null</c> para quitarlo. Viaja con el resto del encabezado y
    /// se persiste con "Guardar cambios", no en una escritura aparte: es una decisión del asesor
    /// sobre el documento, igual que la vigencia o la cuenta de cobro.
    ///
    /// La vista previa manda exactamente este mismo cuerpo, así que la pantalla ve el efecto del
    /// piso sobre cada línea antes de guardar.
    /// </summary>
    int? GlobalScaleFloor { get; }

    /// <summary>
    /// Cotización detal: con <c>true</c> ninguna línea recibe descuento. Viaja al lado del piso y
    /// por el mismo motivo —decisión del owner, 2026-09-23: sin endpoint propio—, y se aplica
    /// **antes** que él en <c>Quotation.UpdateDetails</c>: apagar el detal y elegir un piso en el
    /// mismo cuerpo funciona; prenderlo con un piso no nulo es contradictorio y el dominio lo
    /// rechaza con <c>quotation.retail.floor_not_allowed</c>.
    /// </summary>
    bool IsRetail { get; }
}

/// <summary>
/// Las reglas del encabezado, compartidas por el guardado de una vez y su cálculo previo a través
/// de <see cref="QuotationEditsValidator{TEdits}"/>: dos copias de la misma regla se
/// desincronizan a la primera que alguien toque.
///
/// Es un método genérico y no un validador base porque FluentValidation sólo sabe hacer
/// <c>Include</c> de un validador del **mismo** tipo, y acá los tipos son dos.
/// </summary>
internal static class QuotationHeaderRules
{
    public static void AddTo<TEdits>(AbstractValidator<TEdits> validator)
        where TEdits : IQuotationHeaderEdits
    {
        validator.RuleFor(edits => edits.PaymentMethod)
            .MaximumLength(Quotation.PaymentMethodMaxLength)
            .When(edits => edits.PaymentMethod is not null);
    }
}

/// <summary>
/// Las reglas de campo de las partes, compartidas por la creación, el guardado y su cálculo
/// previo. El dominio ya las hace cumplir con su código
/// (<c>quotation.billing.identification_required</c>); esto existe para que el 422 traiga el campo
/// en el mapa <c>errors</c> —<c>Parties.Billing.IdentificationNumber</c>—, que es lo único con lo
/// que el formulario sabe marcar el input.
///
/// <paramref name="requireBillingIdentification"/> en false es el cálculo previo: la persona
/// todavía está escribiendo, y exigir el número ahí sería un 422 en cada tecla. El tope de largo
/// sí aplica siempre — un número demasiado largo no se arregla escribiendo más.
///
/// La parte de entrega no se valida: el formulario la manda siempre con
/// <c>identificationNumber: ""</c> y el dominio la descarta.
///
/// Interno a propósito: el escaneo de validadores del composition root sólo registra los
/// públicos, y éste no es un punto de entrada sino una pieza de los tres de arriba.
/// </summary>
internal sealed class QuotationPartiesRequestValidator : AbstractValidator<QuotationPartiesRequest>
{
    public QuotationPartiesRequestValidator(bool requireBillingIdentification)
    {
        // Con consumidor final una parte propia es otro error, con su propio código de dominio
        // (quotation.billing.final_consumer_conflict): un 422 de campo lo taparía.
        RuleFor(parties => parties.Billing!.IdentificationNumber)
            .NotEmpty()
            .WithMessage("The identification number is required when billing to someone else.")
            .When(parties =>
                requireBillingIdentification
                && !parties.BillsToFinalConsumer
                && !string.IsNullOrWhiteSpace(parties.Billing?.Name));

        // Contra el valor recortado, que es lo que el dominio guarda y compara.
        RuleFor(parties => parties.Billing!.IdentificationNumber)
            .Must(number => number!.Trim().Length <= QuotationPartyDetails.IdentificationNumberMaxLength)
            .WithMessage(
                $"The identification number cannot exceed {QuotationPartyDetails.IdentificationNumberMaxLength} characters.")
            .When(parties => parties.Billing?.IdentificationNumber is not null);
    }
}

/// <summary>
/// Abstracta a propósito, igual que <see cref="OrderEditsValidator{TEdits}"/>: el escaneo de
/// validadores del composition root sólo registra las dos concretas.
///
/// <b>No exige al menos una línea</b>, a diferencia del pedido: una cotización nace sin líneas y
/// quedarse sin ninguna es un estado legítimo del editor —<c>Quotation.RemoveItem</c> no tiene la
/// regla del último ítem que sí tiene <c>RemoveItemAfterConversion</c>—. Lo que sí bloquea que una
/// cotización vacía avance es <c>EnsureComplete</c> al enviarla, donde corresponde.
/// </summary>
public abstract class QuotationEditsValidator<TEdits> : AbstractValidator<TEdits>
    where TEdits : IQuotationEdits
{
    // El guardado exige el documento de la facturación; el cálculo previo no (ver
    // QuotationPartiesRequestValidator). Es la única regla en la que los dos difieren.
    protected QuotationEditsValidator(bool requireBillingIdentification = true)
    {
        QuotationHeaderRules.AddTo(this);
        RuleFor(edits => edits.Parties!)
            .SetValidator(new QuotationPartiesRequestValidator(requireBillingIdentification))
            .When(edits => edits.Parties is not null);

        RuleForEach(edits => edits.Items).ChildRules(item =>
        {
            item.RuleFor(line => line.ProductId).NotEmpty();
            item.RuleFor(line => line.Quantity).GreaterThan(0m);
        });
        // El dominio ya prohíbe dos líneas del mismo producto (quotation.item.duplicate_product),
        // pero acá la lista es la clave del diff: repetida, una de las dos se perdería en silencio.
        RuleFor(edits => edits.Items)
            .Must(items => items.Select(line => line.ProductId).Distinct().Count() == items.Count)
            .WithMessage("The same product cannot appear more than once.");
    }
}

/// <summary>
/// La compuerta de estado del guardado de una vez y de su cálculo previo. Repite lo que
/// <c>Quotation.EnsureEditable</c> (Quotation.cs:851) ya exige —y con su mismo código— porque hace
/// falta **antes** de tocar el agregado: una cotización ya convertida se edita por
/// <c>PUT /orders/{orderId}</c>, y sin esto el rechazo llegaría recién al primer método de dominio,
/// después de haber ido al catálogo a valorizar líneas que nunca se van a aplicar.
///
/// Mismo criterio que <c>SaveOrderEditsHandler</c> con <c>OrderStatus.Pending</c>.
/// </summary>
internal static class QuotationEditable
{
    public static void Ensure(Quotation quotation)
    {
        if (quotation.Status is QuotationStatus.Draft or QuotationStatus.Sent)
        {
            return;
        }

        throw new QuotationsDomainException(
            "quotation.quotation.not_editable",
            "The quotation cannot be edited in its current status.");
    }
}
