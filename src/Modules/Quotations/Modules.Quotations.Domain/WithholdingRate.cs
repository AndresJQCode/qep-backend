using BuildingBlocks.Domain.Taxes;

namespace Modules.Quotations.Domain;

/// <summary>
/// La foto de una tasa de retención que un documento congela (spec 2026-11-10 §7.5): nombre, tipo, país,
/// porcentaje, base mínima y el valor de la unidad tributaria resuelto para la fecha del documento.
/// Editar la tasa en el catálogo no la mueve; cargar otra UVT sólo mueve la de un documento editable
/// (spec retenciones por UVT §5.4, <c>RefreshTaxUnits</c>).
/// </summary>
/// <param name="TaxRateId">La tasa del catálogo de la que salió; sin FK, la tasa puede borrarse después.</param>
/// <param name="MinimumBase">En unidades tributarias si el país tiene unidad, en dinero si no; null = sin base.</param>
/// <param name="MinimumBaseUnit">El nombre de la unidad en que estaba la base al congelar ("UVT"), para mostrarla.</param>
/// <param name="TaxUnitValue">El valor de la unidad tributaria al congelar; null si no había ninguno cargado.</param>
/// <param name="Percentage">El porcentaje efectivo, el que entra al cálculo (spec ReteICA por empresa §7.5). Es el del
/// catálogo salvo que el asesor lo haya editado en una tasa municipal.</param>
/// <param name="CityId">El municipio de una tasa municipal (ReteICA); null en las nacionales.</param>
public sealed record WithholdingRate(
    Guid TaxRateId,
    string Name,
    string Type,
    string Country,
    decimal Percentage,
    decimal? MinimumBase,
    string? MinimumBaseUnit,
    decimal? TaxUnitValue,
    Guid? CityId = null)
{
    /// <summary>
    /// El porcentaje del catálogo al congelar (plan P3). Arranca igual al efectivo; editar es
    /// <c>rate with { Percentage = p }</c>, que conserva éste.
    /// </summary>
    public decimal CatalogPercentage { get; init; } = Percentage;

    /// <summary>
    /// El año de la UVT congelada en <see cref="TaxUnitValue"/> (spec retenciones por UVT §5.2): menor al del documento
    /// cuando la del año en curso no estaba cargada. Null sin unidad, o en una fila anterior a esta columna que no casó.
    /// </summary>
    public int? TaxUnitYear { get; init; }

    /// <summary>Si el asesor cambió el porcentaje del catálogo en este documento.</summary>
    public bool Overridden => Percentage != CatalogPercentage;

    /// <summary>
    /// Desde qué base aplica: 0 sin base mínima; <c>MinimumBase × TaxUnitValue</c> con unidad tributaria;
    /// <c>MinimumBase</c> en un país sin unidad; null si la base está en unidades y no hay valor, y entonces
    /// nunca aplica. No se redondea (spec §7.5): sólo el monto va a dos decimales.
    /// </summary>
    public decimal? Threshold => MinimumBase switch
    {
        null => 0m,
        { } units when TaxUnitValue is { } unitValue => units * unitValue,
        { } money when TaxCountries.Find(Country)?.TaxUnitName is null => money,
        _ => null,
    };
}

/// <summary>Una tasa que sí aplicó al documento, con la base sobre la que se calculó y su monto.</summary>
public readonly record struct AppliedWithholding(WithholdingRate Rate, decimal Base, decimal Amount);
