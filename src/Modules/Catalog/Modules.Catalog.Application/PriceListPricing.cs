using BuildingBlocks.Domain.Pricing;
using Modules.Catalog.Domain;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

/// <summary>
/// Spec 2026-11-22 (D-12): el único camino de un precio de canal. Los adaptadores de Bootstrapper que antes leían el precio base de
/// la variante (POS, tienda, cotizaciones, mayorista) arman acá un <see cref="PriceBook"/> por request y le preguntan a él. La regla
/// es <see cref="PriceList.PriceFor"/>; esto sólo junta la lista, sus precios fijos y la regla de redondeo de cada moneda.
/// </summary>
public interface IPriceListPricing
{
    /// <summary>La lista asignada al canal, o <c>null</c>: el canal vende al precio base.</summary>
    Task<PriceList?> ListForChannelAsync(Guid tenantId, PriceChannel channel, CancellationToken cancellationToken);

    Task<PriceList?> FindAsync(Guid tenantId, PriceListId id, CancellationToken cancellationToken);

    /// <summary>
    /// Los precios de esas variantes con la lista (<c>null</c> = precio base), en toda moneda en que la variante tenga precio base.
    /// </summary>
    Task<PriceBook> BookAsync(
        Guid tenantId,
        PriceList? list,
        IReadOnlyCollection<PricedVariant> variants,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lo mismo para una lista que todavía no se guardó (la vista previa del distribuidor): sólo el ajuste, sin precios fijos.
    /// </summary>
    Task<PriceBook> BookForAdjustmentAsync(
        Guid tenantId,
        decimal adjustmentPercent,
        IReadOnlyCollection<PricedVariant> variants,
        CancellationToken cancellationToken);
}

/// <summary>Una variante con su precio base por moneda, como lo guarda Catalog.</summary>
public sealed record PricedVariant(Guid VariantId, IReadOnlyDictionary<string, decimal> BasePrices)
{
    public static PricedVariant Of(ProductVariant variant) =>
        new(variant.Id.Value, variant.Prices.ToDictionary(price => price.Currency, price => price.Amount, StringComparer.Ordinal));
}

/// <summary>Un precio de canal y si salió de un precio fijo de la lista (D-2) o del ajuste sobre el base.</summary>
public readonly record struct ListedPrice(decimal Amount, bool IsFixed);

/// <summary>Los precios ya resueltos de las variantes de un request. Una variante o moneda que no está no tiene precio.</summary>
public sealed class PriceBook
{
    private static readonly IReadOnlyDictionary<string, ListedPrice> NoPrices = new Dictionary<string, ListedPrice>();

    private readonly IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, ListedPrice>> _prices;

    internal PriceBook(IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, ListedPrice>> prices) => _prices = prices;

    /// <summary>Sin lista: cada variante a su precio base, ni ajuste ni redondeo. También lo usan los dobles de prueba.</summary>
    public static PriceBook BasePrices(IEnumerable<PricedVariant> variants) =>
        new(variants
            .GroupBy(variant => variant.VariantId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyDictionary<string, ListedPrice>)group.First().BasePrices
                    .ToDictionary(pair => pair.Key, pair => new ListedPrice(pair.Value, false), StringComparer.Ordinal)));

    /// <summary>Por moneda, como el diccionario de precios base que reemplaza.</summary>
    public IReadOnlyDictionary<string, decimal> PricesOf(Guid variantId) =>
        Listed(variantId).ToDictionary(pair => pair.Key, pair => pair.Value.Amount, StringComparer.Ordinal);

    public IReadOnlyDictionary<string, decimal> PricesOf(ProductVariant variant) => PricesOf(variant.Id.Value);

    public decimal? PriceIn(Guid variantId, string currency) =>
        Listed(variantId).TryGetValue(currency, out var price) ? price.Amount : null;

    public decimal? PriceIn(ProductVariant variant, string currency) => PriceIn(variant.Id.Value, currency);

    public ListedPrice? ListedIn(Guid variantId, string currency) =>
        Listed(variantId).TryGetValue(currency, out var price) ? price : null;

    private IReadOnlyDictionary<string, ListedPrice> Listed(Guid variantId) =>
        _prices.TryGetValue(variantId, out var prices) ? prices : NoPrices;
}

public sealed class PriceListPricing(IPriceListRepository priceLists, ITenantPriceRounding priceRounding) : IPriceListPricing
{
    public async Task<PriceList?> ListForChannelAsync(Guid tenantId, PriceChannel channel, CancellationToken cancellationToken)
    {
        var assignment = (await priceLists.ListChannelsAsync(tenantId, cancellationToken))
            .FirstOrDefault(candidate => candidate.Channel == channel);
        return assignment is null ? null : await priceLists.GetAsync(tenantId, assignment.PriceListId, cancellationToken);
    }

    public Task<PriceList?> FindAsync(Guid tenantId, PriceListId id, CancellationToken cancellationToken) =>
        priceLists.GetAsync(tenantId, id, cancellationToken);

    public async Task<PriceBook> BookAsync(
        Guid tenantId, PriceList? list, IReadOnlyCollection<PricedVariant> variants, CancellationToken cancellationToken)
    {
        if (list is null || variants.Count == 0)
        {
            // Sin lista, el canal vende al precio base: ni ajuste ni redondeo, el mismo número que antes de las listas.
            return PriceBook.BasePrices(variants);
        }

        var overrides = await priceLists.FindOverrideAmountsAsync(
            tenantId, list.Id, variants.Select(variant => variant.VariantId).ToList(), cancellationToken);
        return await BuildAsync(tenantId, list.AdjustmentPercent, overrides, variants, cancellationToken);
    }

    public Task<PriceBook> BookForAdjustmentAsync(
        Guid tenantId, decimal adjustmentPercent, IReadOnlyCollection<PricedVariant> variants, CancellationToken cancellationToken) =>
        BuildAsync(tenantId, adjustmentPercent, new Dictionary<(Guid, string), decimal>(), variants, cancellationToken);

    private async Task<PriceBook> BuildAsync(
        Guid tenantId,
        decimal adjustmentPercent,
        IReadOnlyDictionary<(Guid VariantId, string Currency), decimal> overrides,
        IReadOnlyCollection<PricedVariant> variants,
        CancellationToken cancellationToken)
    {
        // Una regla por moneda y por request: un tenant tiene un puñado de monedas.
        var rules = new Dictionary<string, PriceRounding>(StringComparer.Ordinal);
        foreach (var currency in variants.SelectMany(variant => variant.BasePrices.Keys).Distinct(StringComparer.Ordinal))
        {
            rules[currency] = await priceRounding.GetAsync(tenantId, currency, cancellationToken);
        }

        var book = new Dictionary<Guid, IReadOnlyDictionary<string, ListedPrice>>();
        foreach (var variant in variants)
        {
            if (book.ContainsKey(variant.VariantId))
            {
                continue;
            }

            var prices = new Dictionary<string, ListedPrice>(StringComparer.Ordinal);
            foreach (var (currency, basePrice) in variant.BasePrices)
            {
                var fixedPrice = overrides.TryGetValue((variant.VariantId, currency), out var amount) ? amount : (decimal?)null;
                if (PriceList.PriceFor(basePrice, adjustmentPercent, fixedPrice, rules[currency]) is { } price)
                {
                    prices[currency] = new ListedPrice(price, fixedPrice is not null);
                }
            }

            book[variant.VariantId] = prices;
        }

        return new PriceBook(book);
    }
}
