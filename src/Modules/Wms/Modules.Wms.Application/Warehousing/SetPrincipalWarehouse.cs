using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;
using Modules.Wms.Domain.Warehousing;
using static Modules.Wms.Application.WmsAuditActions;

namespace Modules.Wms.Application;

/// <summary>Aplanado como <see cref="UpdateWarehouseCommand"/> (S25).</summary>
public sealed record SetPrincipalWarehouseCommand(Guid TenantId, Guid WarehouseId, long Version) : ICommand<WarehouseDto>
{
    public static SetPrincipalWarehouseCommand From(Guid tenantId, Guid warehouseId, SetPrincipalWarehouseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new(tenantId, warehouseId, request.Version);
    }
}

public sealed class SetPrincipalWarehouseValidator : AbstractValidator<SetPrincipalWarehouseCommand>
{
    public SetPrincipalWarehouseValidator()
    {
        // Sin cuerpo o sin version llega 0: se dice como error de campo y no como un 412 que confunde.
        RuleFor(command => command.Version).GreaterThan(0);
    }
}

/// <summary>
/// Elige la bodega principal del inventario básico (spec §5.3): quita la marca de la anterior y la pone en ésta, que gana su ubicación
/// general si no la tenía. Corre en una transacción y bajo el lock <see cref="LockKey"/>, el mismo que toma quien provisiona la
/// principal en el primer evento: el índice único parcial no es diferible, así que la marca vieja se guarda antes de poner la nueva.
/// Elegir la que ya es principal no cambia nada.
/// </summary>
public sealed class SetPrincipalWarehouseHandler(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    IWarehouseRepository warehouses,
    IWmsGeographyReader geography,
    IWmsUnitOfWork unitOfWork,
    IWmsAuditRecorder auditRecorder,
    IClock clock,
    IValidator<SetPrincipalWarehouseCommand> validator)
    : ICommandHandler<SetPrincipalWarehouseCommand, WarehouseDto>
{
    /// <summary>La clave del lock de transacción por tenant que serializa a quien cambia o provisiona la principal.</summary>
    public static string LockKey(Guid tenantId) => $"wms-principal:{tenantId}";

    public async Task<WarehouseDto> HandleAsync(SetPrincipalWarehouseCommand command, CancellationToken cancellationToken)
    {
        await authorization.EnsureAsync(
            executionContext, command.TenantId, WmsPermissions.WarehouseManage, warehouseId: command.WarehouseId, ct: cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        await unitOfWork.LockAsync(LockKey(command.TenantId), cancellationToken);

        var warehouse = await warehouses.GetAsync(command.TenantId, new WarehouseId(command.WarehouseId), cancellationToken)
            ?? throw WmsNotFound.Warehouse(command.WarehouseId);
        WmsVersions.Ensure(warehouse.Version, command.Version);

        if (warehouse.IsPrincipal)
        {
            return await WarehouseDtoMapper.ToDtoAsync(warehouse, geography, cancellationToken);
        }

        var now = clock.UtcNow;
        var previous = await warehouses.FindPrincipalAsync(command.TenantId, cancellationToken);
        if (previous is not null)
        {
            previous.ClearPrincipal(now);
            Audit(command.TenantId, previous, isPrincipal: false, now);
            // Antes de marcar la nueva: dos filas con la marca chocarían con IX_warehouses_tenant_principal.
            await unitOfWork.FlushAsync(cancellationToken);
        }

        warehouse.MarkPrincipal(now);
        warehouse.EnsureGeneralLocation(now);
        Audit(command.TenantId, warehouse, isPrincipal: true, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await WarehouseDtoMapper.ToDtoAsync(warehouse, geography, cancellationToken);
    }

    private void Audit(Guid tenantId, Warehouse warehouse, bool isPrincipal, DateTimeOffset now) =>
        auditRecorder.Record(
            tenantId,
            executionContext.SubjectId,
            WmsActorType.Human,
            WarehouseUpdated,
            Resources.Warehouse,
            warehouse.Id.Value.ToString(),
            new Dictionary<string, string?>
            {
                ["isPrincipal"] = isPrincipal ? "true" : "false",
                ["generalLocationId"] = warehouse.GeneralLocationId?.Value.ToString(),
            },
            now);
}
