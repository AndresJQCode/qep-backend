using BuildingBlocks.Domain;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Aprobar exige, según la política de campos del cliente del tenant, el documento del cliente facturado y la
/// ciudad a la que se le envía. Es un 422 de dominio con <c>missingOn = "Customer"</c>: lo que falta está en la
/// ficha del cliente, no en el pedido.
/// </summary>
public sealed class BilledCustomerFieldRequiredException(string code, string message)
    : DomainException(code, message),
      IHasProblemExtensions
{
    public IReadOnlyDictionary<string, object?> ProblemExtensions { get; } =
        new Dictionary<string, object?> { ["missingOn"] = "Customer" };
}

internal static class BilledCustomerFieldsRequired
{
    public const string DocumentCode = "order.order.billed_document_required";

    public const string ShippingCityCode = "order.order.shipping_city_required";

    /// <summary>
    /// Sólo aplica cuando se factura a la ficha del cliente: el consumidor final y la fila de facturación propia
    /// no dependen de ella. Lo barato primero: ni el tenant se lee en esos casos, ni el cliente si la política no
    /// exige nada al facturar. Un cliente que ya no resuelve cuenta como sin documento y sin ciudad.
    /// </summary>
    public static async Task EnsureSatisfiedAsync(
        ITenantDirectory tenantDirectory,
        IQuotationCustomerLookup customerLookup,
        Guid tenantId,
        Order order,
        CancellationToken cancellationToken)
    {
        var source = OrderBilledParty.SourceFor(order.BillsToFinalConsumer, order.Billing);
        if (source != BilledPartySource.Customer)
        {
            return;
        }

        var settings = await tenantDirectory.GetRegionalSettingsAsync(new TenantId(tenantId), cancellationToken);
        var policy = settings?.EffectiveCustomerFieldPolicy ?? CustomerFieldPolicy.Default;
        if (!policy.Document.OnBilling && !policy.City.OnBilling)
        {
            return;
        }

        var customer = await customerLookup.FindAsync(tenantId, order.ClientId, cancellationToken);

        if (policy.Document.OnBilling
            && (string.IsNullOrWhiteSpace(customer?.IdentificationType)
                || string.IsNullOrWhiteSpace(customer.IdentificationNumber)))
        {
            throw new BilledCustomerFieldRequiredException(
                DocumentCode,
                "The tenant requires the document of the invoiced customer before approving the order.");
        }

        // La ciudad del cliente sólo importa si el envío va a su dirección: con recogida en tienda, sin flete
        // o con una parte de entrega propia no se usa.
        var shipsToTheCustomer = !order.IsStorePickup
            && order.ShippingDetails != ShippingDetails.None
            && order.Shipping is null;
        if (policy.City.OnBilling && shipsToTheCustomer && !HasCity(customer))
        {
            throw new BilledCustomerFieldRequiredException(
                ShippingCityCode,
                "The tenant requires the city of the invoiced customer before approving an order that ships to them.");
        }
    }

    // Un cliente sin país se trata como colombiano (la ficha vieja): ahí la ciudad es el id; fuera de Colombia, el texto.
    private static bool HasCity(QuotationCustomerRef? customer) =>
        customer is not null
        && (string.IsNullOrEmpty(customer.Country) || customer.Country == "CO"
            ? customer.CityId is not null
            : !string.IsNullOrWhiteSpace(customer.CityName));
}
