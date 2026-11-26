using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.Application;

public sealed record GetScannableLocationsQuery(Guid TenantId, Guid WarehouseId) : IQuery<ScannableLocationsDto>;

/// <summary>Lo que la app de bodega cachea para resolver un código escaneado contra su bodega (P-1, §8). Sin virtuales; el etag
/// deja que el endpoint responda 304.</summary>
public sealed class GetScannableLocationsHandler(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    IWarehouseRepository warehouses)
    : IQueryHandler<GetScannableLocationsQuery, ScannableLocationsDto>
{
    public async Task<ScannableLocationsDto> HandleAsync(GetScannableLocationsQuery query, CancellationToken cancellationToken)
    {
        await authorization.EnsureAsync(
            executionContext, query.TenantId, WmsPermissions.DocumentRead, warehouseId: query.WarehouseId, ct: cancellationToken);

        var warehouse = await warehouses.GetAsync(query.TenantId, new WarehouseId(query.WarehouseId), cancellationToken)
            ?? throw WmsNotFound.Warehouse(query.WarehouseId);

        return LocationDtoMapper.ToScannable(warehouse.Locations);
    }
}
