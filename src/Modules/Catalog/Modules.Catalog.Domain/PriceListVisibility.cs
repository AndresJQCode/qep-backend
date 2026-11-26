namespace Modules.Catalog.Domain;

/// <summary>Qué productos ve el distribuidor con esta lista (sólo el catálogo del distribuidor la lee, spec 2026-11-22 D-7). Viaja
/// por nombre en el JSON.</summary>
public enum PriceListVisibility
{
    /// <summary>Todos los productos activos del catálogo del mayorista.</summary>
    AllActive,

    /// <summary>Sólo los de las categorías elegidas y sus descendientes (D-9).</summary>
    Categories,
}
