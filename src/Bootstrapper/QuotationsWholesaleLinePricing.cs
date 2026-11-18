using Modules.Quotations.Application;
using Modules.Wholesale.Application;

namespace Bootstrapper;

/// <summary>
/// Wholesale → Quotations (mayorista fase 3, D-5): el precio de lista del distribuidor para la línea que el mayorista agrega a
/// su propio pedido. Sin mirar estado de la relación ni visibilidad: lo decide <see cref="WholesaleOffers.ListPricesForOrderAsync"/>.
/// Quien llama es el mayorista sobre su tenant; el comprador sale del pedido guardado, nunca de un cuerpo.
/// </summary>
internal sealed class QuotationsWholesaleLinePricing(WholesaleOffers offers) : IWholesaleLinePricing
{
    public Task<IReadOnlyDictionary<Guid, decimal>> ListPricesAsync(
        Guid tenantId,
        Guid buyerTenantId,
        string currency,
        IReadOnlyCollection<Guid> variantIds,
        CancellationToken cancellationToken) =>
        offers.ListPricesForOrderAsync(tenantId, buyerTenantId, currency, variantIds, cancellationToken);
}
