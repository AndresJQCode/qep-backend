using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Wms.Domain;
using Modules.Wms.Domain.Warehousing;
using static Modules.Wms.Application.WmsAuditActions;

namespace Modules.Wms.Application;

public sealed record DeleteLocationCommand(Guid TenantId, Guid WarehouseId, Guid LocationId) : ICommand<bool>;

/// <summary>P-3: borra una hoja no virtual sin saldo. Las virtuales no se borran (§8) y un padre con hijos tampoco.</summary>
public sealed class DeleteLocationHandler(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    IWarehouseRepository warehouses,
    ILocationStockProbe stockProbe,
    IWmsUnitOfWork unitOfWork,
    IWmsAuditRecorder auditRecorder,
    IClock clock)
    : ICommandHandler<DeleteLocationCommand, bool>
{
    public async Task<bool> HandleAsync(DeleteLocationCommand command, CancellationToken cancellationToken)
    {
        await authorization.EnsureAsync(
            executionContext, command.TenantId, WmsPermissions.WarehouseManage, warehouseId: command.WarehouseId, ct: cancellationToken);

        var warehouse = await warehouses.GetAsync(command.TenantId, new WarehouseId(command.WarehouseId), cancellationToken)
            ?? throw WmsNotFound.Warehouse(command.WarehouseId);
        var id = new LocationId(command.LocationId);
        var location = warehouse.FindLocation(id) ?? throw WmsNotFound.Location(command.LocationId);

        if (await stockProbe.HasStockAsync(command.TenantId, command.LocationId, cancellationToken))
        {
            throw new WmsDomainException(WmsErrorCodes.LocationHasStock, "The location has stock and cannot be removed.");
        }

        // El agregado decide virtual y con hijos; el repositorio sólo le dice a EF que borre la fila.
        warehouse.RemoveLocation(id);
        warehouses.RemoveLocation(location);

        auditRecorder.Record(
            command.TenantId,
            executionContext.SubjectId,
            WmsActorType.Human,
            LocationDeleted,
            Resources.Location,
            location.Id.Value.ToString(),
            new Dictionary<string, string?>
            {
                ["warehouseId"] = warehouse.Id.Value.ToString(),
                ["code"] = location.Code,
                ["path"] = location.Path,
            },
            clock.UtcNow);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return true;
    }
}
