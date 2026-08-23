using Modules.Pricing.Domain;

namespace Modules.Pricing.Application;

internal static class PriceListMapping
{
    public static PriceListDto ToDto(this PriceList priceList) => new(
        priceList.Id.Value,
        priceList.Name,
        priceList.Prefix,
        priceList.IsActive,
        priceList.CreatedAt,
        priceList.UpdatedAt);
}
