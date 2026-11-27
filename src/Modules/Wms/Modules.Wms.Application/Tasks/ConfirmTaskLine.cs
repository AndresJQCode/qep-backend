using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;
using Modules.Wms.Domain;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Tasks;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.Application;

public sealed record ConfirmTaskLineCommand(
    Guid TenantId,
    Guid TaskId,
    Guid LineId,
    Guid LeaseToken,
    Guid FromLocationId,
    Guid ToLocationId,
    Guid? LotId,
    decimal Quantity,
    string? OverrideReason,
    DateTimeOffset? OccurredAt,
    string DeviceId) : ICommand<TaskDto>
{
    public static ConfirmTaskLineCommand From(Guid tenantId, Guid taskId, Guid lineId, ConfirmTaskLineRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new(
            tenantId,
            taskId,
            lineId,
            request.LeaseToken,
            request.FromLocationId,
            request.ToLocationId,
            request.LotId,
            request.Quantity,
            request.OverrideReason,
            request.OccurredAt,
            request.DeviceId ?? string.Empty);
    }
}

/// <summary>La ventana y el piso de <c>OccurredAt</c> no se miran aquí: los aplican la tarea y el ledger, con el código del ledger.</summary>
public sealed class ConfirmTaskLineValidator : AbstractValidator<ConfirmTaskLineCommand>
{
    public const int OverrideReasonMaxLength = 200;

    public ConfirmTaskLineValidator()
    {
        RuleFor(command => command.DeviceId).MustBeADeviceId();
        RuleFor(command => command.Quantity).GreaterThan(0m);
        RuleFor(command => command.OverrideReason)
            .Must(reason => reason is null || reason.Trim().Length <= OverrideReasonMaxLength)
            .WithMessage($"The override reason has at most {OverrideReasonMaxLength} characters.");
        RuleFor(command => command.OccurredAt).NotNull();
    }
}

/// <summary>
/// Confirma parte o todo de una línea y mueve el stock (spec 2026-11-26 §6.8 y §7.9). Es el único camino por el que una tarea escribe
/// al ledger, con una sola llamada por transacción. Ruta IK. Orden de bloqueos: el operario, la tarea y, dentro del ledger, los saldos.
/// El molde común (clave, transacción, locks, lease, cierre, recibo) vive en <see cref="OperatorLineRunner"/>.
/// </summary>
public sealed class ConfirmTaskLineHandler(
    OperatorLineRunner runner,
    IWarehouseRepository warehouses,
    IInventoryLedger ledger,
    IClock clock,
    IValidator<ConfirmTaskLineCommand> validator)
    : ICommandHandler<ConfirmTaskLineCommand, TaskDto>
{
    public async Task<TaskDto> HandleAsync(ConfirmTaskLineCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await runner.AuthorizeAsync(command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var occurredAt = command.OccurredAt!.Value;

        return await runner.RunAsync(
            new OperatorLineRequest(
                command.TenantId,
                command.TaskId,
                command.LineId,
                command.LeaseToken,
                command.DeviceId,
                occurredAt,
                "confirm",
                command),
            (context, ct) => ApplyAsync(command, occurredAt, context, ct),
            cancellationToken);
    }

    private async Task ApplyAsync(
        ConfirmTaskLineCommand command, DateTimeOffset occurredAt, OperatorLineContext context, CancellationToken cancellationToken)
    {
        var (task, line, member) = (context.Task, context.Line, context.MemberId);
        var (to, overrideReason) = await ResolveDestinationAsync(task, line, command, cancellationToken);
        if (line.LotId is { } planned && command.LotId != planned.Value)
        {
            throw new WmsDomainException(WmsErrorCodes.TaskLotMismatch, "El lote escaneado no es el de la línea.");
        }

        var lot = line.LotId?.Value ?? command.LotId;
        task.ConfirmLine(
            member, command.LeaseToken, command.DeviceId, line.Id, command.Quantity, clock.UtcNow, occurredAt, context.MemberIsBusy);

        var owner = task.OwnerId.Value;
        var entry = new LedgerEntry(
            new StockKey(owner, line.FromLocationId.Value, line.VariantId, lot, line.StockStatus),
            new StockKey(owner, to, line.VariantId, lot, line.StockStatus),
            command.Quantity,
            null,
            TaskTypePolicy.MovementReasonOf(task.Type),
            new LedgerDocument("Task", task.Id, line.Id),
            occurredAt,
            overrideReason: overrideReason);
        await ledger.PostAsync(
            new LedgerBatch(
                command.TenantId,
                Guid.CreateVersion7(),
                context.OperationKey,
                new LedgerActor(member, LedgerActorType.Human, command.DeviceId.Trim()),
                [entry]),
            cancellationToken);
    }

    // El origen es siempre el planeado. Otro destino exige motivo, un tipo que lo admita y una ubicación física de la bodega que
    // admita almacenamiento.
    private async Task<(Guid To, string? OverrideReason)> ResolveDestinationAsync(
        WarehouseTask task, WarehouseTaskLine line, ConfirmTaskLineCommand command, CancellationToken cancellationToken)
    {
        if (command.FromLocationId != line.FromLocationId.Value)
        {
            throw Mismatch("El origen escaneado no es el de la línea.");
        }

        if (command.ToLocationId == line.ToLocationId?.Value)
        {
            return (command.ToLocationId, null);
        }

        var reason = string.IsNullOrWhiteSpace(command.OverrideReason) ? null : command.OverrideReason.Trim();
        if (reason is null || !TaskTypePolicy.AllowsDestinationOverride(task.Type))
        {
            throw Mismatch("El destino escaneado no es el de la línea. Para usar otro, escribe el motivo.");
        }

        var warehouse = await warehouses.GetAsync(command.TenantId, task.WarehouseId, cancellationToken)
            ?? throw WmsNotFound.Warehouse(task.WarehouseId.Value);
        var destination = warehouse.FindLocation(new LocationId(command.ToLocationId));
        if (destination is null || destination.IsVirtual || !destination.AllowsStorage || destination.Id == line.FromLocationId)
        {
            throw new WmsDomainException(
                WmsErrorCodes.TaskLocationInvalid,
                "Ese destino no sirve: tiene que ser otra ubicación física de la bodega que admita almacenamiento.");
        }

        return (command.ToLocationId, reason);
    }

    private static WmsDomainException Mismatch(string message) => new(WmsErrorCodes.TaskLocationMismatch, message);
}
