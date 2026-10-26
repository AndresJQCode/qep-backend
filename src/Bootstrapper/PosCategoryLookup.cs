using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Modules.Pos.Application;

namespace Bootstrapper;

/// <summary>
/// Catalog → Pos, las categorías de la caja (spec 2026-10-26 §3.4). El repositorio cuenta los productos
/// activos **directos** de cada categoría; acá se acumulan hacia arriba —una categoría suma los suyos y los
/// de todas sus descendientes—, que es lo que encuentra el filtro de la cuadrícula. El árbol del tenant es
/// pequeño (sin pantalla de administración, a lo sumo 10 niveles), así que se acumula en memoria sobre la
/// lista que ya trajo la consulta. El total es una consulta aparte: la suma de las raíces perdería los
/// productos sin categoría.
/// </summary>
internal sealed class PosCategoryLookup(IProductCategoryRepository categories, IProductRepository products)
    : IPosCategoryLookup
{
    public async Task<PosCategoryList> ListAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var rows = await categories.ListAsync(tenantId, onlyActiveProducts: true, cancellationToken);
        var total = await products.CountActiveAsync(tenantId, cancellationToken);

        var direct = rows.ToDictionary(row => row.Category.Id, row => row.ProductCount);
        var children = rows
            .Where(row => row.Category.ParentId is not null)
            .ToLookup(row => row.Category.ParentId!.Value, row => row.Category.Id);
        var rolledUp = new Dictionary<ProductCategoryId, int>();

        // Orden del repositorio (por nombre), incluidas las de 0: el chip de una categoría vacía se pinta igual.
        return new PosCategoryList(
            rows.Select(row => new PosCategoryRef(
                row.Category.Id.Value,
                row.Category.Name,
                row.Category.ParentId?.Value,
                RollUp(row.Category.Id, direct, children, rolledUp, []))).ToList(),
            total);
    }

    // rollup(c) = directo(c) + Σ rollup(hijas), con memo. El conjunto de la rama en curso corta un ciclo
    // —que el dominio no permite crear— en vez de recorrerlo para siempre.
    private static int RollUp(
        ProductCategoryId id,
        Dictionary<ProductCategoryId, int> direct,
        ILookup<ProductCategoryId, ProductCategoryId> children,
        Dictionary<ProductCategoryId, int> rolledUp,
        HashSet<ProductCategoryId> inProgress)
    {
        if (rolledUp.TryGetValue(id, out var known))
        {
            return known;
        }

        if (!inProgress.Add(id))
        {
            return 0;
        }

        var count = direct.GetValueOrDefault(id)
            + children[id].Sum(child => RollUp(child, direct, children, rolledUp, inProgress));
        inProgress.Remove(id);
        rolledUp[id] = count;
        return count;
    }
}
