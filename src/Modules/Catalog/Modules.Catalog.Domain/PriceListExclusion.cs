namespace Modules.Catalog.Domain;

/// <summary>Qué es lo que una lista de precios le oculta al distribuidor. Viaja por nombre en el JSON.</summary>
public enum PriceListExclusionKind
{
    /// <summary>Una categoría y todas sus descendientes.</summary>
    Category,

    /// <summary>Un producto con todas sus variantes.</summary>
    Product,
}

/// <summary>Una exclusión de la lista (spec 2026-11-20): lo excluido gana siempre sobre el alcance de la lista.</summary>
public readonly record struct PriceListExclusion(PriceListExclusionKind Kind, Guid Id);
