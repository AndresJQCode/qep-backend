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
            warehouse.Locations.Count(location => !location.IsVirtual));
    }
}
