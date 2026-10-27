using FluentValidation;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>Un comprobante nuevo del borrador (spec 2026-09-17). <paramref name="FileId"/> es null
/// en el cálculo previo: los archivos se suben recién al guardar. <paramref name="PaidOn"/> es la
/// fecha del soporte (a pedido, 2026-10-08), ver <see cref="OrderPaymentProofRequest.PaidOn"/>.</summary>
public sealed record OrderEditProofAddition(Guid? FileId, decimal Amount, DateOnly? PaidOn = null);

/// <summary>Los comprobantes del borrador. <see cref="Update"/> sólo lleva los que cambian; los no
/// mencionados se conservan.</summary>
public sealed record OrderEditProofs(
    IReadOnlyList<OrderEditProofAddition> Add,
    IReadOnlyList<OrderPaymentProofUpdateRequest> Update,
    IReadOnlyList<Guid> RemoveIds)
{
    public static readonly OrderEditProofs None = new([], [], []);
}

/// <summary>El estado deseado completo de «Editar pedido» (spec 2026-09-17, decisión 2): mismo
/// cuerpo para el guardado y el cálculo previo, así que las reglas se escriben una vez.</summary>
public interface IOrderEdits
{
    IReadOnlyList<OrderItemAddition> Items { get; }

    OrderEditProofs Proofs { get; }

    string? Notes { get; }

    /// <summary>
    /// El piso de escala global de la cotización del pedido, o <c>null</c> para quitarlo. Viaja
    /// con el resto de la edición y se persiste al guardar: cambiarlo en la pantalla recalcula
    /// contra la vista previa, igual que cambiar una cantidad.
    /// </summary>
    int? GlobalScaleFloor { get; }

    /// <summary>
    /// Cotización detal de la cotización del pedido. Mismo contrato que
    /// <c>IQuotationEdits.IsRetail</c>: viaja con la edición, se aplica antes que el piso y se
    /// persiste al guardar sólo si cambió.
    /// </summary>
    bool IsRetail { get; }

    /// <summary>
    /// El envío del pedido pendiente (spec 2026-10-17 §5.4). <b>Null es "no lo toques"</b>, como en la
    /// cotización: un frontend viejo no lo borra. El pedido no cambia recoger o enviar.
    /// </summary>
    ShippingRequest? Shipping { get; }

    /// <summary>
    /// A quién se le factura (spec 2026-10-26). <b>Null es «no lo toques»</b>, como <see cref="Shipping"/>:
    /// un frontend viejo no borra la facturación del pedido.
    /// </summary>
    OrderBillingEditRequest? Billing { get; }
}

/// <summary>Aplica el bloque <c>billing</c> de la edición al pedido. Lo comparten el guardado y el cálculo
/// previo para que la retención y el excedente de IVA de la vista previa sean los que el guardado dejaría.</summary>
internal static class OrderBillingEdits
{
    /// <summary>Devuelve <c>null</c> si no hay bloque o nada cambió; si cambió, el texto del historial.
    /// <paramref name="enforceFiscalRules"/> es la regla NIT, que sólo el guardado aplica.</summary>
    public static string? Apply(
        Order order,
        OrderBillingEditRequest? billing,
        string? tenantCountry,
        bool enforceFiscalRules,
        MemberId updatedBy,
        DateTimeOffset now)
    {
        if (billing is null)
        {
            return null;
        }

        var party = billing.Party.ToDomain();
        if (enforceFiscalRules)
        {
            BillingPartyFiscalRules.EnsureConsistent(party, tenantCountry);
        }

        var before = OrderChangeSummary.BillingLabel(order);
        var changed = order.SetBilling(
            party, billing.BillsToFinalConsumer, billing.BillingUsesBusinessName, billing.BillingWithRetention,
            billing.BillingVatSurplus, updatedBy, now);
        return changed ? OrderChangeSummary.BillingChanged(before, OrderChangeSummary.BillingLabel(order)) : null;
    }
}

/// <summary>
/// Las reglas de la sección «Validación» del spec 2026-09-17. Abstracta a propósito: el escaneo de
/// validadores del composition root sólo registra las dos concretas. <paramref name="requireFileIds"/>
/// separa el guardado del cálculo previo, que todavía no tiene archivos (el <c>newFileId</c> sí se
/// valida en los dos: si llega, no puede ser <see cref="Guid.Empty"/>).
/// </summary>
public abstract class OrderEditsValidator<TEdits> : AbstractValidator<TEdits>
    where TEdits : IOrderEdits
{
    protected OrderEditsValidator(bool requireFileIds, bool requireBillingIdentification)
    {
        AddBillingRules(requireBillingIdentification);

        RuleFor(edits => edits.Items)
            .Must(items => items.Count > 0)
            .WithMessage("At least one product is required.");
        RuleForEach(edits => edits.Items).ChildRules(item =>
        {
            item.RuleFor(line => line.VariantId).NotEmpty();
            item.RuleFor(line => line.Quantity).GreaterThan(0m);
        });
        RuleFor(edits => edits.Items)
            .Must(items => !HasDuplicates(items.Select(line => line.VariantId)))
            .WithMessage("La misma variante no puede aparecer más de una vez.");

        RuleForEach(edits => edits.Proofs.Add).ChildRules(addition =>
        {
            addition.RuleFor(value => value.Amount).GreaterThan(0m);
            if (requireFileIds)
            {
                addition.RuleFor(value => value.FileId).NotNull().NotEqual(Guid.Empty);
            }
        });
        RuleForEach(edits => edits.Proofs.Update).SetValidator(new OrderPaymentProofUpdateRequestValidator());
        RuleFor(edits => edits.Proofs.Update)
            .Must(updates => !HasDuplicates(updates.Select(update => update.ProofId)))
            .WithMessage("The same payment proof cannot be updated more than once in a request.");
        RuleFor(edits => edits.Proofs)
            .Must(proofs => !proofs.Update.Select(update => update.ProofId).Intersect(proofs.RemoveIds).Any())
            .WithMessage("A payment proof cannot be updated and removed in the same request.");
        // Mismo motivo que AddOrderPaymentProofsValidator (revisión final, I2): un archivo, un adjunto.
        RuleFor(edits => edits.Proofs)
            .Must(proofs => !HasDuplicates(proofs.Add
                .Where(addition => addition.FileId is not null)
                .Select(addition => addition.FileId!.Value)
                .Concat(proofs.Update
                    .Where(update => update.NewFileId is not null)
                    .Select(update => update.NewFileId!.Value))))
            .WithMessage("The same file cannot be attached more than once in a request.");

        RuleFor(edits => edits.Shipping!)
            .SetValidator(new ShippingRequestValidator())
            .When(edits => edits.Shipping is not null);

        // Las notas no tienen tope (plan 2026-10-15, P2): heredan las de la cotización.
    }

    // Las mismas reglas de número y tipo de documento que QuotationPartiesRequestValidator, con la clave que
    // arma FluentValidation para este comando (Billing.Party.*). El guardado las exige; el cálculo previo sólo
    // rechaza lo mal escrito, porque la persona todavía está tecleando.
    private void AddBillingRules(bool requireBillingIdentification)
    {
        RuleFor(edits => edits.Billing!.Party!.IdentificationNumber)
            .NotEmpty()
            .WithMessage("The identification number is required when billing to someone else.")
            .When(edits => requireBillingIdentification && HasNamedOwnParty(edits));

        RuleFor(edits => edits.Billing!.Party!.IdentificationType)
            .NotEmpty()
            .WithMessage("The identification type is required when billing to someone else.")
            .When(edits => requireBillingIdentification && HasNamedOwnParty(edits));

        RuleFor(edits => edits.Billing!.Party!.IdentificationType)
            .Must(type => PartyFiscalTypesParser.SupportedIdentificationTypes
                .Contains(type!.Trim(), StringComparer.OrdinalIgnoreCase))
            .WithMessage("The identification type must be one of NIT, CC, CE or PASAPORTE.")
            .When(edits => !string.IsNullOrWhiteSpace(edits.Billing?.Party?.IdentificationType));

        RuleFor(edits => edits.Billing!.Party!.PersonType)
            .Must(type => PartyFiscalTypesParser.SupportedPersonTypes
                .Contains(type!.Trim(), StringComparer.OrdinalIgnoreCase))
            .WithMessage("The person type must be one of Natural or Juridica.")
            .When(edits => !string.IsNullOrWhiteSpace(edits.Billing?.Party?.PersonType));

        RuleFor(edits => edits.Billing!.Party!.IdentificationNumber)
            .Must(number => number!.Trim().Length <= QuotationPartyDetails.IdentificationNumberMaxLength)
            .WithMessage(
                $"The identification number cannot exceed {QuotationPartyDetails.IdentificationNumberMaxLength} characters.")
            .When(edits => edits.Billing?.Party?.IdentificationNumber is not null);
    }

    // Con consumidor final una parte propia es otro error, con su código de dominio
    // (order.billing.final_consumer_conflict): un 422 de campo lo taparía.
    private static bool HasNamedOwnParty(TEdits edits) =>
        edits.Billing is { BillsToFinalConsumer: false, Party: { } party }
        && !string.IsNullOrWhiteSpace(party.Name);

    private static bool HasDuplicates<TValue>(IEnumerable<TValue> values)
    {
        var seen = new HashSet<TValue>();
        return values.Any(value => !seen.Add(value));
    }
}
