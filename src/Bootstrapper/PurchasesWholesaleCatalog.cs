using Modules.Purchases.Application;
using Modules.Wholesale.Application;

namespace Bootstrapper;

/// <summary>
/// Wholesale → Purchases (mayorista fase 3): el catálogo del mayorista visto por el distribuidor. Todo lo decide
/// <see cref="WholesaleOffers"/> (relación <c>Active</c> entre <b>ese</b> par, visibilidad y precio de lista); sus rechazos
/// (403 <c>wholesale.relationship.inactive</c>, 422 <c>wholesale.product.not_visible</c>) llegan tal cual a Compras, que usa
/// los mismos códigos.
/// </summary>
internal sealed class PurchasesWholesaleCatalog(WholesaleOffers offers) : IWholesaleCatalog
{
    public async Task<IReadOnlyList<WholesaleCategory>> ListCategoriesAsync(
        Guid buyerTenantId, Guid wholesalerTenantId, CancellationToken cancellationToken) =>
        [.. (await offers.ListVisibleCategoriesAsync(wholesalerTenantId, buyerTenantId, cancellationToken))
            .Select(node => new WholesaleCategory(node.Id, node.Name, node.ParentId))];

    public async Task<WholesaleCatalogPage> SearchAsync(
        Guid buyerTenantId,
        Guid wholesalerTenantId,
        string? search,
        Guid? categoryId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var found = await offers.SearchAsync(wholesalerTenantId, buyerTenantId, search, categoryId, page, pageSize, cancellationToken);
        return new WholesaleCatalogPage(
            found.Currency, found.PriceListName, found.DiscountPercent, [.. found.Items.Select(ToItem)], found.Total, found.ShowPrices);
    }

    public async Task<(string Currency, IReadOnlyDictionary<Guid, WholesaleCatalogItem> Items, bool ShowPrices)> PriceAsync(
        Guid buyerTenantId,
        Guid wholesalerTenantId,
        IReadOnlyCollection<Guid> variantIds,
        CancellationToken cancellationToken)
    {
        var (terms, priced) = await offers.PriceVisibleAsync(wholesalerTenantId, buyerTenantId, variantIds, cancellationToken);
        return (terms.Currency, priced.ToDictionary(pair => pair.Key, pair => ToItem(pair.Value)), terms.ShowPrices);
    }

    private static WholesaleCatalogItem ToItem(WholesaleOffer offer) =>
        new(offer.VariantId, offer.Code, offer.Name, offer.BasePrice, offer.DiscountPercent, offer.ListPrice, offer.TaxRatePercent,
            offer.ProductId, offer.ProductName, offer.ImageUrl);
}
