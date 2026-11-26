using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;
using Modules.Wms.Domain.Warehousing;
using static Modules.Wms.Application.WmsAuditActions;

namespace Modules.Wms.Application;

/// <summary>Aplanado como <see cref="CreateWarehouseCommand"/> (S25).</summary>
public sealed record UpdateWarehouseCommand(Guid TenantId, Guid WarehouseId, string Name, Guid? CityId, bool IsActive, long Version)
    : ICommand<WarehouseDto>
{
    public static UpdateWarehouseCommand From(Guid tenantId, Guid warehouseId, UpdateWarehouseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new(tenantId, warehouseId, request.Name ?? string.Empty, request.CityId, request.IsActive, request.Version);
    }
}

public sealed class UpdateWarehouseValidator : AbstractValidator<UpdateWarehouseCommand>
{
    public UpdateWarehouseValidator()
    {
        RuleFor(command => command.Name).MustBeAWarehouseName();
    }
}

public sealed class UpdateWarehouseHandler(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    IWarehouseRepository warehouses,
    IWmsGeographyReader geography,
    IWmsUnitOfWork unitOfWork,
    IWmsAuditRecorder auditRecorder,
    IClock clock,
    IValidator<UpdateWarehouseCommand> validator)
    : ICommandHandler<UpdateWarehouseCommand, WarehouseDto>
{
    public async Task<WarehouseDto> HandleAsync(UpdateWarehouseCommand command, CancellationToken cancellationToken)
    {
        await authorization.EnsureAsync(
            executionContext, command.TenantId, WmsPermissions.WarehouseManage, warehouseId: command.WarehouseId, ct: cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var warehouse = await warehouses.GetAsync(command.TenantId, new WarehouseId(command.WarehouseId), cancellationToken)
            ?? throw WmsNotFound.Warehouse(command.WarehouseId);
        WmsVersions.Ensure(warehouse.Version, command.Version);
        await WarehouseRules.EnsureCityAsync(geography, command.CityId, cancellationToken);

        var before = (warehouse.Name, warehouse.CityId, warehouse.IsActive);
        var now = clock.UtcNow;
        warehouse.Update(command.Name, command.CityId, command.IsActive, now);

        // Sólo lo que cambió: el reporte de auditoría pregunta «qué campo», no «cómo quedó la bodega».
        var changed = new Dictionary<string, string?>();
        if (before.Name != warehouse.Name)
        {
            changed["name"] = warehouse.Name;
        }

        if (before.CityId != warehouse.CityId)
        {
            changed["cityId"] = warehouse.CityId?.ToString();
        }

        if (before.IsActive != warehouse.IsActive)
        {
            changed["isActive"] = warehouse.IsActive ? "true" : "false";
        }

        auditRecorder.Record(
            command.TenantId,
            executionContext.SubjectId,
            WmsActorType.Human,
            WarehouseUpdated,
            Resources.Warehouse,
            warehouse.Id.Value.ToString(),
            changed,
            now);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await WarehouseDtoMapper.ToDtoAsync(warehouse, geography, cancellationToken);
    }
}
