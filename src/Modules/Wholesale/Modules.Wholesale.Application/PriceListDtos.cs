namespace Modules.Wholesale.Application;

// Spec 2026-11-22: las listas viven en Catalog (/catalog/price-lists). Mayorista conserva la vista previa «así lo ve el
// distribuidor» y las categorías para elegir la visibilidad, porque las dos se arman con el lector del catálogo del distribuidor.

/// <summary>
/// Una variante como la ve el distribuidor: el precio base en la moneda del mayorista y el de lista ya redondeado con su regla.
/// La pantalla nunca multiplica.
/// </summary>
public sealed record PriceListPreviewItemDto(Guid VariantId, string Code, string Name, decimal BasePrice, decimal ListPrice);

/// <summary>
/// «Así lo ve el distribuidor» mientras se edita la lista: hasta 20 variantes y cuántos productos entran en total, para que la
/// pantalla diga «y N más» sin otra llamada. <c>HiddenProducts</c> son los productos del alcance que las exclusiones esconden. Sólo
/// aplica el ajuste: los precios fijos de la lista se ven en su propia tabla (spec 2026-11-22, D-9).
/// </summary>
public sealed record PriceListPreviewDto(
    string Currency, int TotalProducts, IReadOnlyList<PriceListPreviewItemDto> Items, int HiddenProducts = 0);

/// <summary>
/// Una categoría del catálogo del mayorista para elegir la visibilidad de una lista (ruling W5): el árbol plano con
/// <c>ParentId</c>, que la pantalla anida. Existe para no exigir <c>catalog.product.read</c> a quien administra listas.
/// </summary>
public sealed record WholesaleCategoryDto(Guid Id, string Name, Guid? ParentId);

/// <summary>Todas las categorías del mayorista ordenadas por nombre.</summary>
public sealed record WholesaleCategoriesResponse(IReadOnlyList<WholesaleCategoryDto> Items);
