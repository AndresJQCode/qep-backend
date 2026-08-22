namespace Modules.Catalog.Domain;

/// <summary>
/// Qué exige una escala de precio sobre la cantidad pedida. Obligatoria y mutuamente
/// excluyente con su campo asociado: <c>Multiple</c> exige <see cref="PriceScale.Multiple"/>
/// y prohíbe <see cref="PriceScale.PackagingUnit"/>, y al revés para <c>PackagingUnit</c>.
/// </summary>
public enum PriceScaleRestriction
{
    Multiple,
    PackagingUnit
}
