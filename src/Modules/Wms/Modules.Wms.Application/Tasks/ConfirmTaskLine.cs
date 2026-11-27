using System.Diagnostics;
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
/// </summary>
public sealed class ConfirmTaskLineHandler(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    WmsCaller caller,
    IWmsUnitOfWork unitOfWork,
    IdempotencyScope scope,
    IOperationReceipts receipts,
    IWarehouseTaskRepository tasks,
    IWarehouseRepository warehouses,
    OperatorTaskGuard guard,
    IInventoryLedger ledger,
    IWmsConfirmMetrics metrics,
    IClock clock,
    WarehouseTaskDtoComposer composer,
    IValidator<ConfirmTaskLineCommand> validator)
    : ICommandHandler<ConfirmTaskLineCommand, TaskDto>
{
    public async Task<TaskDto> HandleAsync(ConfirmTaskLineCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await authorization.EnsureAsync(
            executionContext, command.TenantId, WmsPermissions.DocumentRead, ct: cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var key = scope.Key ?? throw new InvalidOperationException("Confirming a task line must run behind an idempotency key.");
        var member = await caller.RequireAsync(command.TenantId, cancellationToken);

        var started = Stopwatch.GetTimestamp();
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        await unitOfWork.LockAsync(TaskMemberLocks.For(member), cancellationToken);
        if (!await tasks.LockAsync(command.TenantId, command.TaskId, cancellationToken))
        {
            throw WmsNotFound.Task(command.TaskId);
        }

        var task = await tasks.GetAsync(command.TenantId, command.TaskId, cancellationToken)
            ?? throw WmsNotFound.Task(command.TaskId);
        await guard.EnsureCanPerformAsync(command.TenantId, task, cancellationToken);
        var lease = OperatorTaskGuard.EnsureLease(task, member, command.LeaseToken);

        // El dominio trata una línea ajena como error de cableado: el 404 sale de acá.
        var line = task.FindLine(command.LineId) ?? throw WmsNotFound.TaskLine(command.LineId);
        var (to, overrideReason) = await ResolveDestinationAsync(task, line, command, cancellationToken);
        if (line.LotId is { } planned && command.LotId != planned.Value)
        {
            throw new WmsDomainException(WmsErrorCodes.TaskLotMismatch, "El lote escaneado no es el de la línea.");
        }

        var lot = line.LotId?.Value ?? command.LotId;
        var occurredAt = command.OccurredAt!.Value;
        // DA-32: bajo el lock de la persona, si mientras tanto tomó otra tarea ésta no vuelve a InProgress. Lo que movió en el piso se
        // anota igual.
        var busy = lease == LeaseCheck.Resumable
            && await tasks.FindInProgressOfMemberAsync(command.TenantId, member, cancellationToken) is not null;
        task.ConfirmLine(
            member, command.LeaseToken, command.DeviceId, line.Id, command.Quantity, clock.UtcNow, occurredAt, busy);

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
                key,
                new LedgerActor(member, LedgerActorType.Human, command.DeviceId.Trim()),
                [entry]),
            cancellationToken);

        var dto = await composer.ComposeAsync(task, member, withEvents: false, cancellationToken);
        receipts.Record(dto);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        metrics.RecordConfirmDuration(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return dto;
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
