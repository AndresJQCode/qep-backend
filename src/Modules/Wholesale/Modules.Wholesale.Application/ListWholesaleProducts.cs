using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Wholesale.Application;

/// <summary>Spec 2026-11-20: <c>PageSize</c> se recorta a <see cref="ListWholesaleProductsHandler.MaxPageSize"/>.</summary>
public sealed record ListWholesaleProductsQuery(Guid TenantId, string? Search, Guid? CategoryId, int Page, int PageSize)
    : IQuery<WholesaleProductsResponse>;

/// <summary>
/// Un producto del selector de «qué ocultar». <c>Code</c> es el de su única variante (con varias, <c>null</c> y la pantalla pinta
/// «N variantes» con <c>VariantCount</c>); <c>CategoryName</c> viaja resuelto para no cruzar ids en el cliente.
/// </summary>
public sealed record WholesaleProductDto(
    Guid Id, string? Code, string Name, Guid? CategoryId, string? CategoryName, int VariantCount, string? ImageUrl);

/// <summary><c>Total</c> cuenta productos, no variantes: la página es de productos.</summary>
public sealed record WholesaleProductsResponse(IReadOnlyList<WholesaleProductDto> Items, int Total, int Page, int PageSize);

/// <summary>
/// Spec 2026-11-20: los productos que una lista puede mostrar (activos y con precio en la moneda por defecto del mayorista), para
/// elegir cuáles ocultar. Mismo permiso que las listas y el mismo puerto de lectura del catálogo: quien administra listas no
/// necesita <c>catalog.product.read</c> (ruling W5, como <see cref="ListWholesaleCategoriesHandler"/>).
/// </summary>
public sealed class ListWholesaleProductsHandler(
    IExecutionContext executionContext,
    ITenantDefaultCurrency defaultCurrency,
    IWholesaleCatalogReader catalog)
    : IQueryHandler<ListWholesaleProductsQuery, WholesaleProductsResponse>
{
    public const int MaxPageSize = 50;

    /// <summary>Tope de la página para que <c>(page - 1) * pageSize</c> no desborde un <c>int</c>; más allá, la página vacía.</summary>
    public const int MaxPage = 100_000;

    public async Task<WholesaleProductsResponse> HandleAsync(ListWholesaleProductsQuery query, CancellationToken cancellationToken)
    {
        WholesaleAuthorization.EnsureAuthorized(executionContext, query.TenantId, WholesalePermissions.PriceListManage);

        var page = Math.Clamp(query.Page, 1, MaxPage);
        var pageSize = query.PageSize < 1 ? MaxPageSize : Math.Min(query.PageSize, MaxPageSize);
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
        var currency = await defaultCurrency.GetAsync(query.TenantId, cancellationToken);

        var (found, total) = await catalog.SearchProductsAsync(
            query.TenantId, currency, query.CategoryId, search, page, pageSize, cancellationToken);

        var categoryIds = found.Where(product => product.CategoryId is not null).Select(product => product.CategoryId!.Value).Distinct().ToArray();
        var categories = categoryIds.Length == 0
            ? new Dictionary<Guid, WholesaleCategoryRef>()
            : await catalog.FindCategoriesAsync(query.TenantId, categoryIds, cancellationToken);

        return new WholesaleProductsResponse(
            found
                .Select(product => new WholesaleProductDto(
                    product.Id,
                    product.Code,
                    product.Name,
                    product.CategoryId,
                    product.CategoryId is { } categoryId && categories.TryGetValue(categoryId, out var category) ? category.Name : null,
                    product.VariantCount,
                    product.ImageUrl))
                .ToList(),
            total,
            page,
            pageSize);
    }
}
