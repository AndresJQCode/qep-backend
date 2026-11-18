namespace BuildingBlocks.Domain.Catalog;

/// <summary>La naturaleza de un producto del catálogo (spec 2026-11-18 §3.1). Viaja por nombre. Vive en BuildingBlocks
/// porque Catalog la define y Quotations la congela en la línea, y los dos módulos no se ven.</summary>
public enum ProductKind
{
    Physical,
    Service,
}
