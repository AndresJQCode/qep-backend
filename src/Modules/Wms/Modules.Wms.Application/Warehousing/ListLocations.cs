using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.Application;

public sealed record ListLocationsQuery(Guid TenantId, Guid WarehouseId) : IQuery<LocationsDto>;

/// <summary>El árbol plano de la bodega, con las virtuales: la pantalla de ubicaciones las muestra de solo lectura.</summary>
public sealed class ListLocationsHandler(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    IWarehouseRepository warehouses)
    : IQueryHandler<ListLocationsQuery, LocationsDto>
{
    public async Task<LocationsDto> HandleAsync(ListLocationsQuery query, CancellationToken cancellationToken)
    {
        await authorization.EnsureAsync(
            executionContext, query.TenantId, WmsPermissions.DocumentRead, warehouseId: query.WarehouseId, ct: cancellationToken);

        var warehouse = await warehouses.GetAsync(query.TenantId, new WarehouseId(query.WarehouseId), cancellationToken)
            ?? throw WmsNotFound.Warehouse(query.WarehouseId);

        return LocationDtoMapper.ToDto(warehouse.Locations);
    }
}
