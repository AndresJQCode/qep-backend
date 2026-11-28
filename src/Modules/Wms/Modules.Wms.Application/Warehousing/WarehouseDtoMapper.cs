using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.Application;

internal static class WarehouseDtoMapper
{
    public static WarehouseDto ToDto(Warehouse warehouse, string? cityName)
    {
        ArgumentNullException.ThrowIfNull(warehouse);

        return new WarehouseDto(
            warehouse.Id.Value,
            warehouse.Code,
            warehouse.Name,
            warehouse.CityId,
            cityName,
            warehouse.IsActive,
            warehouse.Version,
            warehouse.Locations.Count(location => !location.IsVirtual),
            warehouse.IsPrincipal,
            warehouse.GeneralLocationId?.Value);
    }

    /// <summary>Resuelve el nombre de la ciudad y arma el DTO: lo que devuelven el alta y la edición.</summary>
    public static async Task<WarehouseDto> ToDtoAsync(Warehouse warehouse, IWmsGeographyReader geography, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(warehouse);
        ArgumentNullException.ThrowIfNull(geography);

        string? cityName = null;
        if (warehouse.CityId is { } cityId)
        {
            var cities = await geography.FindCitiesAsync([cityId], cancellationToken);
            cityName = cities.TryGetValue(cityId, out var city) ? city.Name : null;
        }

        return ToDto(warehouse, cityName);
    }
}
