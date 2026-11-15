using BuildingBlocks.Domain.Taxes;
using Modules.Catalog.Domain;

namespace Modules.Catalog.Application;

internal static class TaxRateMapping
{
    /// <param name="city">El municipio ya resuelto por <see cref="ITaxRateGeographyLookup"/>; null en una tasa nacional o si
    /// la ciudad no se encontró.</param>
    public static TaxRateDto ToDto(this TaxRate taxRate, TaxRateCityRef? city) => new(
        taxRate.Id.Value,
        taxRate.Name,
        taxRate.Percentage,
        taxRate.IsActive,
        taxRate.CreatedAt,
        taxRate.UpdatedAt,
        taxRate.Type,
        taxRate.Country,
        taxRate.Behavior.ToString(),
        taxRate.MinimumBase,
        // La unidad sólo se informa con base: sin base no hay en qué unidad leerla.
        taxRate.MinimumBase is null ? null : TaxCountries.Find(taxRate.Country)?.TaxUnitName,
        taxRate.Scope.ToString(),
        taxRate.CityId,
        city is null ? null : new TaxRateCityDto(city.Id, city.Name, city.DepartmentId, city.DepartmentName));

    /// <summary>Una tasa con su municipio resuelto: los handlers que devuelven una sola tasa pasan por acá (P8).</summary>
    public static async Task<TaxRateDto> ToDtoAsync(
        this TaxRate taxRate, ITaxRateGeographyLookup geography, CancellationToken cancellationToken) =>
        (await ToDtosAsync([taxRate], geography, cancellationToken))[0];

    /// <summary>Varias tasas con sus municipios, en una sola consulta a Geography.</summary>
    public static async Task<IReadOnlyList<TaxRateDto>> ToDtosAsync(
        IReadOnlyList<TaxRate> taxRates,
        ITaxRateGeographyLookup geography,
        CancellationToken cancellationToken)
    {
        var cityIds = taxRates
            .Where(taxRate => taxRate.CityId is not null)
            .Select(taxRate => taxRate.CityId!.Value)
            .Distinct()
            .ToArray();
        var cities = await geography.FindCitiesAsync(cityIds, cancellationToken);

        return taxRates
            .Select(taxRate => taxRate.ToDto(
                taxRate.CityId is { } cityId && cities.TryGetValue(cityId, out var city) ? city : null))
            .ToArray();
    }
}
