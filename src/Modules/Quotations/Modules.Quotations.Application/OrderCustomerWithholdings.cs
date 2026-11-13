using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Las tasas que congela un pedido que no nace de una cotización (spec impuestos por tipo §7.5): manual, de la tienda
/// (excepción de D16: aunque siempre trae parte propia) y de una integración. Son las del cliente, pero sólo las del país
/// de la empresa facturadora: un cliente con tasas de varios países no le pasa al pedido las que nunca podrían aplicar.
/// Sin empresa o sin país no hay contra qué filtrar y el pedido nace sin ninguna (D22). Nunca lanza por el cliente: uno
/// sin tasas da una lista vacía, así el webhook de Shopify no empieza a fallar.
/// </summary>
internal static class OrderCustomerWithholdings
{
    public static async Task<IReadOnlyList<WithholdingRate>> ResolveAsync(
        IQuotationWithholdingLookup lookup,
        Guid tenantId,
        Guid customerId,
        QuotationBillingAccount? billingAccount,
        DateOnly documentDate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        var country = billingAccount?.Country?.Trim();
        return string.IsNullOrEmpty(country)
            ? []
            : await lookup.ForCustomerAsync(tenantId, customerId, country, documentDate, cancellationToken);
    }
}
