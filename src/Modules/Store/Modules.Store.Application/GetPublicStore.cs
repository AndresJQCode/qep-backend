using BuildingBlocks.Application;

namespace Modules.Store.Application;

public sealed record GetPublicStoreQuery(string Host) : IQuery<PublicStoreDto>;

public sealed class GetPublicStoreHandler(
    StorefrontResolver resolver,
    IStoreFileLookup files,
    IStoreCatalogReader catalog)
    : IQueryHandler<GetPublicStoreQuery, PublicStoreDto>
{
    public async Task<PublicStoreDto> HandleAsync(GetPublicStoreQuery query, CancellationToken cancellationToken)
    {
        var (store, tenant) = await resolver.ResolveAsync(query.Host, cancellationToken);

        var bannerUrl = store.BannerFileId is { } bannerId
            ? await files.FindPublicImageAsync(store.TenantId, bannerId, cancellationToken)
            : null;
        var regional = tenant.Regional;

        return new PublicStoreDto(
            store.Name ?? tenant.DisplayName,
            tenant.LogoUrl,
            store.ResolveColors() is { } colors ? StoreColorMapping.ToDto(colors) : null,
            new PublicBannerDto(bannerUrl, store.BannerText),
            PublicStoreContactDto.From(store.Contact),
            store.DataPolicyUrl,
            store.PricingMode.ToString(),
            store.CurrenciesForVisitors(),
            store.PrimaryCurrency,
            store.SalesCountries.ToList(),
            await FeaturedAsync(store, cancellationToken),
            new PublicRegionalFormatDto(
                regional.TimeZone, regional.DateFormat, regional.NumberFormat, regional.DefaultCurrency, regional.Country));
    }

    // Una destacada sobrevive si tiene productos (acumulados) en CUALQUIER moneda de la tienda; la app la cruza después con la que el
    // visitante eligió. Las borradas se descartan sin avisar y el orden guardado manda.
    private async Task<IReadOnlyList<StoreFeaturedCategoryDto>> FeaturedAsync(
        Domain.OnlineStore store, CancellationToken cancellationToken)
    {
        if (store.FeaturedCategoryIds.Count == 0)
        {
            return [];
        }

        var tree = (await catalog.CategoryTreeAsync(store.TenantId, cancellationToken))
            .Select(node => (node.Id, node.Name, node.ParentId))
            .ToList();
        var withProducts = new HashSet<Guid>();
        foreach (var currency in store.CurrenciesForVisitors())
        {
            var direct = await catalog.CategoriesAsync(store.TenantId, currency, cancellationToken);
            withProducts.UnionWith(PublicCategoryTree.Build(direct, tree).Select(node => node.Id));
        }

        var names = tree.ToDictionary(node => node.Id, node => node.Name);
        return store.FeaturedCategoryIds
            .Where(id => withProducts.Contains(id) && names.ContainsKey(id))
            .Select(id => new StoreFeaturedCategoryDto(id, names[id]))
            .ToList();
    }
}
