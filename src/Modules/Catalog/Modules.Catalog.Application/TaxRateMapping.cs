using BuildingBlocks.Domain.Taxes;
using Modules.Catalog.Domain;

namespace Modules.Catalog.Application;

internal static class TaxRateMapping
{
    public static TaxRateDto ToDto(this TaxRate taxRate) => new(
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
        taxRate.MinimumBase is null ? null : TaxCountries.Find(taxRate.Country)?.TaxUnitName);
}
