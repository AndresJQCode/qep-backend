using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Las tasas que congela un pedido que no nace de una cotización (spec impuestos por tipo §7.5): manual, de la tienda
/// (excepción de D16: aunque siempre trae parte propia) y de una integración. Son las del cliente, pero sólo las del país
/// de la empresa facturadora: un cliente con tasas de varios países no le pasa al pedido las que nunca podrían aplicar.
/// Sin empresa o sin país no hay contra qué filtrar y el pedido nace sin ninguna (D22).
///
/// Más las municipales por la regla del cliente (spec ReteICA por empresa §7.5, R5/R6/A10): el pedido nace en el municipio
/// de la empresa, con la tasa que la empresa tiene ahí si el cliente es agente en ese municipio.
///
/// Nunca lanza por el cliente ni por la resolución municipal: uno sin tasas da una lista vacía, una empresa sin tasa en su
/// municipio o un cliente sin lista de agentes dan sólo las nacionales, así el webhook de Shopify y el checkout de la tienda
/// no empiezan a fallar (baseline D22).
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
        if (string.IsNullOrEmpty(country))
        {
            return [];
        }

        var national = await lookup.ForCustomerAsync(tenantId, customerId, country, documentDate, cancellationToken);
        if (billingAccount!.CityId is not { } cityId)
        {
            return national;
        }

        // El municipio de la empresa (A10): no hay nada que comprobar contra Geography, así que este armado no lanza 422.
        var municipal = await MunicipalContexts.ForCityAsync(
            lookup, tenantId, customerId, billingAccount, cityId, documentDate, cancellationToken);
        return
        [
            .. national,
            .. municipal.Offers
                .Select(offer => MunicipalWithholdings.ByCustomerRule(offer, municipal.OperationCityId))
                .OfType<WithholdingRate>(),
        ];
    }
}
