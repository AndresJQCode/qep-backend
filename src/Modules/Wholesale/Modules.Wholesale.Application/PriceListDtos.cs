using Modules.Wholesale.Domain;

namespace Modules.Wholesale.Application;

/// <summary>Una categoría visible de la lista con su nombre ya resuelto: la pantalla no cruza ids con el catálogo.</summary>
public sealed record PriceListCategoryDto(Guid Id, string Name);

/// <summary>
/// Una fila de la lista de listas. Trae <c>CreatedAt</c> y <c>Categories</c> (vacía con <c>AllActive</c>) para pintar
/// «Creada 02/10/2026» y «2 categorías · Hogar, Cocina» sin pedir el detalle (adenda del contrato). <c>CategoryCount</c> es
/// <c>Categories.Count</c>: cuenta sólo las que siguen existiendo (ruling W4, D-23). <c>DistributorCount</c> cuenta los
/// distribuidores en cualquier estado: es lo que impide borrarla.
/// </summary>
public sealed record PriceListSummaryDto(
    Guid Id,
    string Name,
    decimal DiscountPercent,
    string Visibility,
    int CategoryCount,
    IReadOnlyList<PriceListCategoryDto> Categories,
    int DistributorCount,
    long Version,
    DateTimeOffset CreatedAt,
    bool ShowPrices = true);

/// <summary>
/// La lista completa para el formulario y su detalle (BFF). <c>Categories</c> trae sólo las que siguen existiendo, ordenadas
/// por nombre (D-23): el detalle no puede pintar una categoría sin nombre, y el siguiente <c>PUT</c> guarda el conjunto limpio.
/// <c>Visibility</c> va por nombre (<c>AllActive</c> o <c>Categories</c>). <c>ShowPrices</c> (spec 2026-11-19): con <c>false</c>
/// el distribuidor de la lista no recibe montos; la pantalla pinta el interruptor sin inferirlo de nada más.
/// </summary>
public sealed record PriceListDetailDto(
    Guid Id,
    string Name,
    decimal DiscountPercent,
    string Visibility,
    IReadOnlyList<PriceListCategoryDto> Categories,
    int DistributorCount,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool ShowPrices = true);

/// <summary>Todas las listas del tenant ordenadas por nombre, sin paginar (D-12): un mayorista tiene un puñado.</summary>
public sealed record PriceListsResponse(IReadOnlyList<PriceListSummaryDto> Items);

/// <summary>
/// Una variante como la ve el distribuidor: el precio base en la moneda del mayorista y el de lista ya redondeado con su regla
/// (D-10). La pantalla nunca multiplica.
/// </summary>
public sealed record PriceListPreviewItemDto(Guid VariantId, string Code, string Name, decimal BasePrice, decimal ListPrice);

/// <summary>
/// «Así lo ve el distribuidor» mientras se edita la lista (D-11): hasta 20 variantes y cuántos productos entran en total,
/// para que la pantalla diga «y N más» sin otra llamada.
/// </summary>
public sealed record PriceListPreviewDto(string Currency, int TotalProducts, IReadOnlyList<PriceListPreviewItemDto> Items);

/// <summary>
/// Una categoría del catálogo del mayorista para elegir la visibilidad de una lista (ruling W5): el árbol plano con
/// <c>ParentId</c>, que la pantalla anida. Existe para no exigir <c>catalog.product.read</c> a quien administra listas.
/// </summary>
public sealed record WholesaleCategoryDto(Guid Id, string Name, Guid? ParentId);

/// <summary>Todas las categorías del mayorista ordenadas por nombre.</summary>
public sealed record WholesaleCategoriesResponse(IReadOnlyList<WholesaleCategoryDto> Items);

/// <summary>Arma los DTOs de lista; las categorías que ya no existen se caen (D-23).</summary>
internal static class PriceListMapping
{
    public static IReadOnlyList<PriceListCategoryDto> CategoriesOf(
        PriceList list, IReadOnlyDictionary<Guid, WholesaleCategoryRef> found) =>
        list.CategoryIds
            .Where(found.ContainsKey)
            .Select(id => new PriceListCategoryDto(id, found[id].Name))
            .OrderBy(category => category.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(category => category.Id)
            .ToList();

    public static PriceListDetailDto ToDetail(
        PriceList list, IReadOnlyDictionary<Guid, WholesaleCategoryRef> found, int distributorCount) =>
        new(
            list.Id.Value,
            list.Name,
            list.DiscountPercent,
            list.Visibility.ToString(),
            CategoriesOf(list, found),
            distributorCount,
            list.Version,
            list.CreatedAt,
            list.UpdatedAt,
            list.ShowPrices);

    public static PriceListSummaryDto ToSummary(
        PriceList list, IReadOnlyDictionary<Guid, WholesaleCategoryRef> found, int distributorCount)
    {
        var categories = CategoriesOf(list, found);
        return new PriceListSummaryDto(
            list.Id.Value,
            list.Name,
            list.DiscountPercent,
            list.Visibility.ToString(),
            categories.Count,
            categories,
            distributorCount,
            list.Version,
            list.CreatedAt,
            list.ShowPrices);
    }
}
