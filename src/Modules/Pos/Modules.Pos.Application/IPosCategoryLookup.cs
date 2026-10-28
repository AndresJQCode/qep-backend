namespace Modules.Pos.Application;

/// <summary>
/// Catalog → Pos (adaptador en Bootstrapper): las categorías de la caja (spec 2026-10-26 §3.4).
/// <see cref="PosCategoryRef.ProductCount"/> ya viene acumulado —productos activos de la categoría y de
/// todas sus descendientes— y <see cref="PosCategoryList.Total"/> son los productos activos del tenant con
/// o sin categoría. Los calcula el adaptador, nunca el navegador (spec §3.4, §7.1).
/// </summary>
public interface IPosCategoryLookup
{
    /// <summary>Todas las categorías del tenant en orden por nombre, incluidas las que no tienen productos.</summary>
    Task<PosCategoryList> ListAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <param name="ParentId">null en una raíz.</param>
/// <param name="ProductCount">Activos de la categoría y de todas sus descendientes: lo que encuentra el filtro.</param>
/// <param name="QuickAccessPosition">Lugar entre los chips de acceso rápido (1..N); null si no está fijada.</param>
public sealed record PosCategoryRef(Guid Id, string Name, Guid? ParentId, int ProductCount, int? QuickAccessPosition);

/// <param name="Total">Activos del tenant con o sin categoría; no es la suma de las raíces.</param>
public sealed record PosCategoryList(IReadOnlyList<PosCategoryRef> Items, int Total);
