using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Modules.Wholesale.Application;
using WholesalePriceListId = Modules.Wholesale.Domain.PriceListId;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-11-22 (D-7, D-11): la lista de un distribuidor es una lista de Catalog. Mayorista la lee por
/// <see cref="IWholesalePriceLists"/>, y el precio sale del mismo resolutor que usan los canales (<see cref="IPriceListPricing"/>),
/// así la regla de D-2 existe una sola vez.
/// </summary>
internal sealed class WholesalePriceLists(IPriceListRepository priceLists, IPriceListPricing pricing) : IWholesalePriceLists
{
    public async Task<WholesalePriceList?> GetAsync(Guid tenantId, WholesalePriceListId id, CancellationToken cancellationToken) =>
        await priceLists.GetAsync(tenantId, new PriceListId(id.Value), cancellationToken) is { } list ? ToWholesale(list) : null;

    public async Task<IReadOnlyList<WholesalePriceList>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
        (await priceLists.ListAsync(tenantId, cancellationToken)).Select(ToWholesale).ToList();

    public async Task<IReadOnlyDictionary<Guid, WholesaleListedPrice>> PriceAsync(
        Guid tenantId,
        WholesalePriceList list,
        string currency,
        IReadOnlyDictionary<Guid, decimal> basePrices,
        CancellationToken cancellationToken)
    {
        var variants = basePrices
            .Select(pair => new PricedVariant(pair.Key, new Dictionary<string, decimal>(StringComparer.Ordinal) { [currency] = pair.Value }))
            .ToList();

        // Una lista sin id es la de la vista previa, que todavía no se guardó: sólo el ajuste.
        PriceBook book;
        if (list.Id.Value == Guid.Empty)
        {
            book = await pricing.BookForAdjustmentAsync(tenantId, list.AdjustmentPercent, variants, cancellationToken);
        }
        else
        {
            var stored = await pricing.FindAsync(tenantId, new PriceListId(list.Id.Value), cancellationToken);
            book = stored is null
                ? await pricing.BookForAdjustmentAsync(tenantId, list.AdjustmentPercent, variants, cancellationToken)
                : await pricing.BookAsync(tenantId, stored, variants, cancellationToken);
        }

        var prices = new Dictionary<Guid, WholesaleListedPrice>();
        foreach (var variant in variants)
        {
            if (book.ListedIn(variant.VariantId, currency) is { } listed)
            {
                prices[variant.VariantId] = new WholesaleListedPrice(listed.Amount, listed.IsFixed);
            }
        }

        return prices;
    }

    private static WholesalePriceList ToWholesale(PriceList list) =>
        new(
            new WholesalePriceListId(list.Id.Value),
            list.Name,
            list.AdjustmentPercent,
            list.Visibility == PriceListVisibility.AllActive ? null : list.CategoryIds.ToArray(),
            list.ExcludedCategoryIds.ToArray(),
            list.ExcludedProductIds.ToArray(),
            list.ShowPrices);
}
