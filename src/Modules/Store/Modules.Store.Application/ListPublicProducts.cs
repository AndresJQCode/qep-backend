using BuildingBlocks.Application;

namespace Modules.Store.Application;

public sealed record ListPublicProductsQuery(string Host, string? Currency, Guid? Category, string? Q, int Page, int PageSize)
    : IQuery<PublicProductsPageDto>;

public sealed class ListPublicProductsHandler(StorefrontResolver resolver, IStoreCatalogReader catalog)
    : IQueryHandler<ListPublicProductsQuery, PublicProductsPageDto>
{
    public const int DefaultPageSize = 24;

    public const int MaxPageSize = 60;

    public const int MaxSearchLength = 100;

    /// <summary>Con el tope de página de 60, la posición más lejana (600 000) cabe de sobra en un int; sin él, <c>(page - 1) * pageSize</c> se desborda y Postgres rechaza el OFFSET negativo.</summary>
    public const int MaxPage = 10_000;

    public static int ClampPage(int page) => Math.Clamp(page, 1, MaxPage);

    public async Task<PublicProductsPageDto> HandleAsync(ListPublicProductsQuery query, CancellationToken cancellationToken)
    {
        var (store, _) = await resolver.ResolveAsync(query.Host, cancellationToken);
        var currency = StorefrontResolver.RequireCurrency(store, query.Currency);

        // Una página fuera de rango se corrige y no se rechaza: es una lectura pública y el visitante no puede arreglar un 422.
        var page = ClampPage(query.Page);
        var pageSize = query.PageSize <= 0 ? DefaultPageSize : Math.Min(query.PageSize, MaxPageSize);
        var text = string.IsNullOrWhiteSpace(query.Q) ? null : query.Q.Trim();
        if (text is { Length: > MaxSearchLength })
        {
            text = text[..MaxSearchLength];
        }

        var (items, total) = await catalog.SearchAsync(
            store.TenantId, currency, text, query.Category, page, pageSize, cancellationToken);
        return new PublicProductsPageDto(
            currency,
            items.Select(card => new PublicProductCardDto(
                card.Id, card.Name, card.CategoryId, card.ImageUrl, card.PriceFrom, card.PriceTo, card.HasVariants, card.VariantId)).ToList(),
            total,
            page,
            pageSize);
    }
}
