namespace Modules.Catalog.Domain;

/// <summary>
/// Qué exige una escala de precio sobre la cantidad pedida. Obligatoria —salvo en la escala
/// incompleta que deja la copia, ver <see cref="PriceScale.Restriction"/>—. <c>Multiple</c> exige
/// <see cref="PriceScale.Multiple"/>; <c>PackagingUnit</c> lo prohíbe y no lleva número propio:
/// usa los empaques del producto (<see cref="Product.PackagingUnits"/>), que no pueden estar
/// vacíos mientras alguna escala la use.
/// </summary>
public enum PriceScaleRestriction
{
    Multiple,
    PackagingUnit
}
