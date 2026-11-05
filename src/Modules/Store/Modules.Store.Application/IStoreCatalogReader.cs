namespace Modules.Store.Application;

/// <summary>
/// Lo que la tienda lee del catálogo: los ajustes (nombres de categorías, monedas con precio) y las lecturas públicas. Las públicas
/// sólo ven productos activos y visibles en la tienda, con variantes activas que tengan precio en la moneda pedida.
/// </summary>
public interface IStoreCatalogReader
{
    /// <summary>Nombre de cada categoría del tenant que exista; un id borrado o ajeno simplemente no aparece.</summary>
    Task<IReadOnlyDictionary<Guid, string>> CategoryNamesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>Las monedas con al menos un precio en el tenant, la fuente de <c>GET /catalog/currencies-in-use</c>.</summary>
    Task<IReadOnlyCollection<string>> CurrenciesInUseAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// Una página de productos por nombre. <paramref name="q"/> busca sólo en el nombre del producto: el código y el código de barras
    /// de una variante nunca salen de la tienda, así que tampoco pueden servir para encontrar un producto. Una categoría expande a ella
    /// y a todas sus descendientes; una que no existe da una página vacía, no un error.
    /// </summary>
    Task<(IReadOnlyList<StoreProductCard> Items, int Total)> SearchAsync(
        Guid tenantId, string currency, string? q, Guid? categoryId, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>Los productos visibles por categoría **directa** (sin descendientes), en esa moneda. El acumulado lo arma <see cref="PublicCategoryTree"/>.</summary>
    Task<IReadOnlyList<StoreCategoryCount>> CategoriesAsync(Guid tenantId, string currency, CancellationToken cancellationToken);

    /// <summary>Todas las categorías del tenant (id, nombre, padre), por nombre: la entrada de <see cref="PublicCategoryTree"/>.</summary>
    Task<IReadOnlyList<StoreCategoryNode>> CategoryTreeAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// El producto con sus variantes vendibles en esa moneda, o null si la tienda no lo muestra (inexistente, oculto, inactivo o sin precio
    /// en la moneda). Con <paramref name="includeScales"/> falso las escalas salen vacías.
    /// </summary>
    Task<StoreProductView?> ProductAsync(
        Guid tenantId, Guid productId, string currency, bool includeScales, CancellationToken cancellationToken);

    /// <summary>
    /// De esas variantes, las que la tienda puede vender en esa moneda: activa, de un producto activo y visible, con precio en la moneda.
    /// Las demás no aparecen.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, StoreSellableVariant>> SellableVariantsAsync(
        Guid tenantId, string currency, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken);
}

/// <param name="HasVariants">Más de una variante vendible en la moneda: desde la tarjeta no se puede elegir por el visitante.</param>
public sealed record StoreProductCard(
    Guid Id, string Name, Guid? CategoryId, string? ImageUrl, decimal PriceFrom, decimal PriceTo, bool HasVariants);

public sealed record StoreCategoryCount(Guid CategoryId, int ProductCount);

public sealed record StoreCategoryNode(Guid Id, string Name, Guid? ParentId);

/// <param name="Restriction"><c>multiple</c> o <c>packaging_unit</c>, como en el catálogo.</param>
public sealed record StoreScaleView(
    int FromUnit, int ToUnit, decimal Discount, decimal Final, string Restriction, int? Multiple, bool AllowGrouping);

/// <param name="PackagingUnits">Sólo con escalas; las escalas <c>packaging_unit</c> los usan como cantidades válidas.</param>
public sealed record StoreVariantView(
    Guid Id,
    string Name,
    string? ImageUrl,
    decimal Price,
    IReadOnlyList<int> PackagingUnits,
    IReadOnlyList<StoreScaleView> Scales);

public sealed record StoreProductView(
    Guid Id,
    string Name,
    string? Description,
    Guid? CategoryId,
    string? ImageUrl,
    IReadOnlyList<string> Gallery,
    IReadOnlyList<StoreVariantView> Variants);

public sealed record StoreSellableVariant(Guid VariantId, Guid ProductId, string Name, string? ImageUrl);
