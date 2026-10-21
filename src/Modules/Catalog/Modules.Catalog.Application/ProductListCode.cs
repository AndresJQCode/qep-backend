namespace Modules.Catalog.Application;

/// <summary>
/// Lo que el listado sabe de las variantes de un producto, ya agregado en la base: cuántas hay y el menor
/// código, en total y entre las activas. Con una sola, el menor código es el suyo.
/// </summary>
public sealed record ProductVariantCodeStats(int Count, string? MinCode, int ActiveCount, string? MinActiveCode);

/// <summary>
/// El <c>code</c> de una fila del listado (<see cref="ProductListItemDto"/>): la grilla pinta el código o, si
/// no hay uno que represente al producto, «N variantes» con <c>variantCount</c>.
///
/// Con una sola variante, su código. Sin opciones, el de su única variante activa aunque guarde inactivas:
/// es lo que queda al quitarle todas las opciones a un producto, y se vende como uno simple (decisión del
/// owner, 2026-10-21). Con opciones y varias variantes, ninguno.
/// </summary>
public static class ProductListCode
{
    public static string? For(bool hasOptions, ProductVariantCodeStats stats)
    {
        ArgumentNullException.ThrowIfNull(stats);

        if (stats.Count == 1)
        {
            return stats.MinCode;
        }

        return !hasOptions && stats.ActiveCount == 1 ? stats.MinActiveCode : null;
    }
}
