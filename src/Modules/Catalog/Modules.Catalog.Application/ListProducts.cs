using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

/// <summary>
/// <c>Name</c>/<c>Code</c> son las dos cajas separadas del listado (CAT-FILTROS-01), cada una
/// filtrando su propia columna con ILIKE — se combinan con AND cuando se llenan las dos.
/// <c>Search</c> es el criterio OR original: nombre del producto, <c>Code</c> de alguna variante o,
/// desde la Tarea 12 de variantes, código de barras de alguna variante (la búsqueda del POS usa la
/// misma consulta, spec 2026-10-20 §5). Sigue existiendo porque el combobox de productos de
/// <c>quotes</c> (<c>useQuoteProducts</c>) necesita un único cuadro de texto libre, no dos — mismo
/// criterio que <c>ListCustomersQuery</c> con el combobox de clientes. Las cajas <c>Name</c> y
/// <c>Code</c> no miran el código de barras.
///
/// <c>IsActive</c> es un filtro real contra la base — antes el `Select` de estado del listado
/// filtraba en el cliente, sobre el catálogo entero ya traído, algo que dejó de alcanzar en
/// cuanto el listado empezó a paginar server-side.
/// </summary>
public sealed record ListProductsQuery(
    Guid TenantId,
    string? Search,
    string? Name,
    string? Code,
    bool? IsActive,
    int Page,
    int PageSize) : IQuery<ProductPage>;

/// <summary>Una página de productos con el total que la UI necesita para paginar.</summary>
public sealed record ProductPage(
    IReadOnlyList<ProductListItemDto> Items,
    int Total,
    int Page,
    int PageSize);

/// <summary>Mismos defaults que `CustomerPaging` — no hay una razón para que el tamaño de
/// página "normal" difiera entre módulos.</summary>
public static class ProductPaging
{
    public const int DefaultPageSize = 50;

    public const int MaxPageSize = 200;

    public static int NormalizePage(int page) => page < 1 ? 1 : page;

    public static int NormalizePageSize(int pageSize) => pageSize switch
    {
        < 1 => DefaultPageSize,
        > MaxPageSize => MaxPageSize,
        _ => pageSize
    };
}

public sealed class ListProductsHandler(
    IProductRepository repository,
    IProductImageLookup imageLookup,
    IExecutionContext executionContext)
    : IQueryHandler<ListProductsQuery, ProductPage>
{
    public async Task<ProductPage> HandleAsync(
        ListProductsQuery query,
        CancellationToken cancellationToken)
    {
        CatalogAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, CatalogPermissions.ProductRead);

        var page = ProductPaging.NormalizePage(query.Page);
        var pageSize = ProductPaging.NormalizePageSize(query.PageSize);

        var (products, total) = await repository.SearchSummariesAsync(
            query.TenantId,
            query.Search,
            query.Name,
            query.Code,
            query.IsActive,
            page,
            pageSize,
            cancellationToken);

        // Las URLs se resuelven en una sola consulta para toda la página, y se vuelve a comparar el
        // tenant de cada archivo: ver ProductMapping.ToDtosAsync.
        var imageIds = products
            .Select(product => product.ImageFileId)
            .OfType<Guid>()
            .Distinct()
            .ToArray();
        var images = imageIds.Length == 0
            ? new Dictionary<Guid, ProductImageRef>()
            : await imageLookup.FindManyAsync(imageIds, cancellationToken);
        var lookups = ProductReadLookups.Empty(query.TenantId) with { Images = images };

        var items = products
            .Select(product => new ProductListItemDto(
                product.Id.Value,
                product.Name,
                product.Code,
                product.IsActive,
                product.Description,
                product.ImageFileId,
                lookups.UrlOf(product.ImageFileId),
                product.TaxRateId?.Value,
                product.VariantCount,
                // Catalogue order (spec D10), como los precios del detalle.
                product.PriceRanges
                    .OrderBy(range => Currencies.OrderOf(range.Currency))
                    .ToDictionary(range => range.Currency, range => new PriceRangeResponse(range.Min, range.Max)),
                product.Version,
                product.CreatedAt,
                product.UpdatedAt))
            .ToArray();

        return new ProductPage(items, total, page, pageSize);
    }
}
