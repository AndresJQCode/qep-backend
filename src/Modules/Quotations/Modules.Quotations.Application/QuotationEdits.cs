using FluentValidation;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>Lo del encabezado que el <c>PATCH</c> y el guardado de una vez tienen en común. Existe
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
}

/// <summary>
/// Las reglas del encabezado, compartidas por <see cref="UpdateQuotationValidator"/> y por
/// <see cref="QuotationEditsValidator{TEdits}"/>: el <c>PUT</c> es el <c>PATCH</c> más las líneas,
/// y dos copias de la misma regla se desincronizan a la primera que alguien toque.
///
/// Es un método genérico y no un validador base porque FluentValidation sólo sabe hacer
/// <c>Include</c> de un validador del **mismo** tipo, y acá los tipos son tres.
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
    protected QuotationEditsValidator()
    {
        QuotationHeaderRules.AddTo(this);

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
