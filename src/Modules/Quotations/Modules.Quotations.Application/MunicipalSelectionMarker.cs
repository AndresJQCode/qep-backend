using BuildingBlocks.Domain.Taxes;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// El porcentaje que el asesor quiere para la retención municipal de un tipo (spec ReteICA por empresa §7.7, §9.4).
/// <paramref name="Percentage"/> null = volver al del catálogo.
/// </summary>
public sealed record WithholdingPercentageRequest(string Type, decimal? Percentage);

/// <summary>
/// Valida la marca «Sin retención» de un cuerpo (owner 2026-11-13, plan P12) antes de tocar el agregado: sólo tipos
/// municipales del país de la empresa, y nunca junto con un id municipal ni un porcentaje no nulo del mismo tipo. Se valida
/// siempre, también cuando el cuerpo cambia la empresa o el municipio: a diferencia de los ids de A11, una marca
/// contradictoria no se ignora en silencio.
/// </summary>
public static class MunicipalSelectionMarker
{
    /// <param name="clearedTypes">Los tipos marcados; null o vacío = sin marca, y no se valida nada.</param>
    /// <param name="companyCountry">El país de la empresa que queda tras el cuerpo.</param>
    /// <param name="municipalSelectorTypes">Los tipos de los ids municipales del cuerpo, coincidan o no con la tasa de la
    /// empresa (<see cref="WithholdingSelection.MunicipalTypesOfAsync"/>).</param>
    /// <param name="percentages">Las entradas de <c>withholdingPercentages</c> del mismo cuerpo.</param>
    /// <param name="codePrefix"><c>quotation</c> u <c>order</c>.</param>
    public static void Validate(
        IReadOnlyCollection<string>? clearedTypes,
        string? companyCountry,
        IReadOnlyCollection<string> municipalSelectorTypes,
        IReadOnlyList<WithholdingPercentageRequest>? percentages,
        string codePrefix)
    {
        ArgumentNullException.ThrowIfNull(municipalSelectorTypes);
        if (clearedTypes is null || clearedTypes.Count == 0)
        {
            return;
        }

        var municipalTypes = TaxCountries.MunicipalTypes(companyCountry?.Trim())
            .Select(definition => definition.Type)
            .ToHashSet(StringComparer.Ordinal);
        if (clearedTypes.Any(type => !municipalTypes.Contains(type)))
        {
            throw new QuotationsDomainException(
                $"{codePrefix}.billing.withholding_type_invalid",
                "Only municipal withholding types of the billing company's country can be cleared.");
        }

        var percentageTypes = (percentages ?? [])
            .Where(entry => entry.Percentage is not null)
            .Select(entry => entry.Type);
        if (clearedTypes.Intersect(municipalSelectorTypes.Concat(percentageTypes), StringComparer.Ordinal).Any())
        {
            throw new QuotationsDomainException(
                $"{codePrefix}.billing.withholding_selection_conflict",
                "A cleared withholding type cannot also be selected or given a percentage in the same request.");
        }
    }
}
