using BuildingBlocks.Domain.Taxes;

namespace Modules.Quotations.Domain;

/// <summary>
/// La foto de una tasa de retención que un documento congela (spec 2026-11-10 §7.5): nombre, tipo, país,
/// porcentaje, base mínima y el valor de la unidad tributaria resuelto para la fecha del documento.
/// Editar la tasa en el catálogo o cargar otra UVT no la mueve.
/// </summary>
/// <param name="TaxRateId">La tasa del catálogo de la que salió; sin FK, la tasa puede borrarse después.</param>
/// <param name="MinimumBase">En unidades tributarias si el país tiene unidad, en dinero si no; null = sin base.</param>
/// <param name="MinimumBaseUnit">El nombre de la unidad en que estaba la base al congelar ("UVT"), para mostrarla.</param>
/// <param name="TaxUnitValue">El valor de la unidad tributaria al congelar; null si no había ninguno cargado.</param>
public sealed record WithholdingRate(
    Guid TaxRateId,
    string Name,
    string Type,
    string Country,
    decimal Percentage,
    decimal? MinimumBase,
    string? MinimumBaseUnit,
    decimal? TaxUnitValue)
{
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

    // Interino: la retención fija de antes (2,5 % sin base) mientras Quotation y Order siguen leyendo
    // AppliesRetention. Las Tareas 11 y 12 la reemplazan por la foto congelada y la borran.
    internal static WithholdingRate LegacyRetefuente { get; } =
        new(Guid.Empty, "Retefuente 2,5 %", TaxCountries.RetefuenteType, TaxCountries.Colombia.Country, 2.5m, null, null, null);
}

/// <summary>Una tasa que sí aplicó al documento, con la base sobre la que se calculó y su monto.</summary>
public readonly record struct AppliedWithholding(WithholdingRate Rate, decimal Base, decimal Amount);
