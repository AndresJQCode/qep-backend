using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

/// <summary>
/// <c>Name</c>/<c>Code</c> son las dos cajas separadas del listado (CAT-FILTROS-01), cada una
/// filtrando su propia columna con ILIKE — se combinan con AND cuando se llenan las dos.
/// <c>Search</c> es el criterio OR original: nombre del producto, <c>Code</c> de alguna variante o,
/// desde las variantes, código de barras de alguna variante (la búsqueda del POS usa la
/// misma consulta, spec 2026-10-20 §5). Sigue existiendo porque el combobox de productos de
/// <c>quotes</c> (<c>useQuoteProducts</c>) necesita un único cuadro de texto libre, no dos — mismo
/// criterio que <c>ListCustomersQuery</c> con el combobox de clientes. Las cajas <c>Name</c> y
/// <c>Code</c> no miran el código de barras.
///
/// <c>IncludeVariants</c> (spec 2026-10-20 §6) agrega a cada ítem sus variantes vendibles para el
/// selector de productos de cotizaciones y pedidos, que así elige y agrega la línea sin otra
/// llamada. Es opt-in: la grilla del catálogo no necesita las variantes y no paga su costo. Con
/// el parámetro, la página se topa en <see cref="ProductPaging.MaxPageSizeWithVariants"/>.
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
    int PageSize,
    bool IncludeVariants = false) : IQuery<ProductPage>;

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

    /// <summary>
    /// El tope cuando el listado trae las variantes. Un producto puede tener 2.048 variantes, cada
    /// una con sus precios y escalas: 200 productos serían más de 400.000 variantes en una
    /// respuesta. Con 50 el peor caso es 102.400, y el selector pide 40 (<c>QUOTE_PRODUCT_PAGE_SIZE</c>
    /// en el frontend). Lo que se topa es la página, no el pedido: la respuesta trae el
    /// <c>pageSize</c> aplicado.
    /// </summary>
    public const int MaxPageSizeWithVariants = 50;

    public static int NormalizePage(int page) => page < 1 ? 1 : page;

    public static int NormalizePageSize(int pageSize, bool includeVariants = false)
    {
        var max = includeVariants ? MaxPageSizeWithVariants : MaxPageSize;
        return pageSize switch
        {
            < 1 => DefaultPageSize,
            _ when pageSize > max => max,
            _ => pageSize
        };
    }
}

public sealed class ListProductsHandler(
    IProductRepository repository,
    IProductImageLookup imageLookup,
    IOptionDefinitionRepository optionRepository,
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
        var pageSize = ProductPaging.NormalizePageSize(query.PageSize, query.IncludeVariants);

        var (products, total) = await repository.SearchSummariesAsync(
            query.TenantId,
            query.Search,
            query.Name,
            query.Code,
            query.IsActive,
            categoryIds: null,
            page,
            pageSize,
            cancellationToken);

        // Las variantes vendibles de los productos activos de la página, en una lectura proyectada
        // (ver IProductRepository.ListActiveVariantsAsync). Un producto dado de baja no se vende,
        // así que ni se consulta: trae la lista vacía.
        var variantsByProduct = query.IncludeVariants
            ? (await repository.ListActiveVariantsAsync(
                    query.TenantId,
                    products.Where(product => product.IsActive).Select(product => product.Id).ToArray(),
                    cancellationToken))
                .GroupBy(variant => variant.ProductId)
                .ToDictionary(group => group.Key, group => group.ToArray())
            : null;

        // Las URLs se resuelven en una sola consulta para toda la página —las principales y las
        // fotos propias de las variantes—, y se vuelve a comparar el tenant de cada archivo: ver
        // ProductMapping.ToDtosAsync.
        var imageIds = products
            .Select(product => product.ImageFileId)
            .Concat(variantsByProduct?.Values.SelectMany(group => group).Select(variant => variant.GalleryFileId) ?? [])
            .OfType<Guid>()
            .Distinct()
            .ToArray();
        var images = imageIds.Length == 0
            ? new Dictionary<Guid, ProductImageRef>()
            : await imageLookup.FindManyAsync(imageIds, cancellationToken);
        var lookups = ProductReadLookups.Empty(query.TenantId) with { Images = images };

        // Los nombres de los valores de opción sólo se piden si alguna variante tiene valores.
        var valueNames = variantsByProduct is not null
            && variantsByProduct.Values.Any(group => group.Any(variant => variant.OptionValueIds.Count > 0))
            ? await ProductMapping.LoadValueNamesAsync(optionRepository, query.TenantId, cancellationToken)
            : new Dictionary<Guid, string>();

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
                product.CategoryId?.Value,
                product.VariantCount,
                // Catalogue order (spec D10), como los precios del detalle.
                product.PriceRanges
                    .OrderBy(range => Currencies.OrderOf(range.Currency))
                    .ToDictionary(range => range.Currency, range => new PriceRangeResponse(range.Min, range.Max)),
                product.Version,
                product.CreatedAt,
                product.UpdatedAt,
                variantsByProduct is null
                    ? null
                    : variantsByProduct.GetValueOrDefault(product.Id, [])
                        .Select(variant => variant.ToSellableDto(
                            product.Name, valueNames, lookups.UrlOf(product.ImageFileId), lookups))
                        .ToArray()))
            .ToArray();

        return new ProductPage(items, total, page, pageSize);
    }
}
