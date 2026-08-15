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
        taxRate.UpdatedAt);
}
