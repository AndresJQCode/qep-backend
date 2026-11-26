using BuildingBlocks.Application;

namespace Modules.Store.Application;

/// <param name="Currency">Anulable en el borde: una moneda ausente llega como 422 <c>store.public.currency_invalid</c>, no como un 400 del binder.</param>
public sealed record ListPublicCategoriesQuery(string Host, string? Currency) : IQuery<PublicCategoriesDto>;

public sealed class ListPublicCategoriesHandler(StorefrontResolver resolver, IStoreCatalogReader catalog)
    : IQueryHandler<ListPublicCategoriesQuery, PublicCategoriesDto>
{
    public async Task<PublicCategoriesDto> HandleAsync(ListPublicCategoriesQuery query, CancellationToken cancellationToken)
    {
        var (store, _) = await resolver.ResolveAsync(query.Host, cancellationToken);
        var currency = StorefrontResolver.RequireCurrency(store, query.Currency);

        var direct = await catalog.CategoriesAsync(store.TenantId, currency, cancellationToken);
        var all = (await catalog.CategoryTreeAsync(store.TenantId, cancellationToken))
            .Select(node => (node.Id, node.Name, node.ParentId))
            .ToList();
        return new PublicCategoriesDto(currency, PublicCategoryTree.Build(direct, all));
    }
}
