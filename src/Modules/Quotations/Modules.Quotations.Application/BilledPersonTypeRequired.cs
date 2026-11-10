using BuildingBlocks.Domain;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Aprobar exige el tipo de persona de a quien se factura (spec 2026-10-26). Es un 422 de dominio con
/// <c>missingOn</c> para que la pantalla sepa a dónde mandar a la persona: a la fila de facturación del
/// pedido (<c>"BillingParty"</c>) o a la ficha del cliente (<c>"Customer"</c>).
/// </summary>
public sealed class BilledPersonTypeRequiredException(string missingOn)
    : DomainException(
        BilledPersonTypeRequired.Code,
        "The tenant requires the person type of whoever is invoiced before approving the order."),
      IHasProblemExtensions
{
    public IReadOnlyDictionary<string, object?> ProblemExtensions { get; } =
        new Dictionary<string, object?> { ["missingOn"] = missingOn };
}

internal static class BilledPersonTypeRequired
{
    public const string Code = "order.order.billed_person_type_required";

    public const string MissingOnBillingParty = "BillingParty";

    public const string MissingOnCustomer = "Customer";

    /// <summary>
    /// Sólo con el requisito efectivo <c>OnOrderApproval</c> (vale en todos los países). El
    /// consumidor final está exento. Lo barato primero: no se lee el tenant para un consumidor final.
    /// </summary>
    public static async Task EnsureSatisfiedAsync(
        ITenantDirectory tenantDirectory,
        IQuotationCustomerLookup customerLookup,
        Guid tenantId,
        Order order,
        CancellationToken cancellationToken)
    {
        var billing = order.Billing;
        var source = OrderBilledParty.SourceFor(order.BillsToFinalConsumer, billing);
        if (source == BilledPartySource.FinalConsumer)
        {
            return;
        }

        var settings = await tenantDirectory.GetRegionalSettingsAsync(new TenantId(tenantId), cancellationToken);
        if (settings?.EffectivePersonTypeRequirement != PersonTypeRequirement.OnOrderApproval)
        {
            return;
        }

        if (source == BilledPartySource.BillingParty)
        {
            if (billing!.PersonType is null)
            {
                throw new BilledPersonTypeRequiredException(MissingOnBillingParty);
            }

            return;
        }

        // La ficha vigente, no una copia: el pedido sin fila de facturación factura a lo que el cliente
        // es hoy. Un cliente que ya no resuelve no se puede dar por bueno.
        var customer = await customerLookup.FindAsync(tenantId, order.ClientId, cancellationToken);
        if (string.IsNullOrWhiteSpace(customer?.PersonType))
        {
            throw new BilledPersonTypeRequiredException(MissingOnCustomer);
        }
    }
}
