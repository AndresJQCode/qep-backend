using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Wms.Application;

public sealed record ListWarehousesQuery(Guid TenantId, bool IncludeInactive) : IQuery<WarehousesDto>;

/// <summary>Las bodegas del tenant por código, con el nombre de su ciudad resuelto en lote (una consulta a Geography, no una por
/// bodega).</summary>
public sealed class ListWarehousesHandler(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    IWmsProvisioner provisioner,
    IWarehouseRepository warehouses,
    IWmsGeographyReader geography)
    : IQueryHandler<ListWarehousesQuery, WarehousesDto>
{
    public async Task<WarehousesDto> HandleAsync(ListWarehousesQuery query, CancellationToken cancellationToken)
    {
        await authorization.EnsureAsync(executionContext, query.TenantId, WmsPermissions.DocumentRead, ct: cancellationToken);
        // §10.1: Tenancy no avisa al prender el módulo, así que el primer listado provisiona el Self y las unidades. Es la única
        // excepción a «un GET no escribe»; va después de autorizar para que un 403 no escriba nada.
        await provisioner.EnsureAsync(query.TenantId, cancellationToken);

        var found = await warehouses.ListAsync(query.TenantId, query.IncludeInactive, cancellationToken);
        var cityIds = found.Where(warehouse => warehouse.CityId.HasValue).Select(warehouse => warehouse.CityId!.Value).Distinct().ToArray();
        var cities = await geography.FindCitiesAsync(cityIds, cancellationToken);

        return new WarehousesDto(found
            .Select(warehouse => WarehouseDtoMapper.ToDto(
                warehouse,
                warehouse.CityId is { } cityId && cities.TryGetValue(cityId, out var city) ? city.Name : null))
            .ToList());
    }
}
