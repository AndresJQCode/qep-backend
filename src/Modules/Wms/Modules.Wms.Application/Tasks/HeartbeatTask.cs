using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Wms.Application;

public sealed record HeartbeatTaskCommand(Guid TenantId, Guid TaskId, Guid LeaseToken) : ICommand<TaskLeaseDto>;

/// <summary>El latido: la app lo manda mientras la tarea está en pantalla. Extiende el lease sin versión ni evento.</summary>
public sealed class HeartbeatTaskHandler(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    WmsCaller caller,
    IWmsUnitOfWork unitOfWork,
    IWarehouseTaskRepository tasks,
    OperatorTaskGuard guard,
    IClock clock)
    : ICommandHandler<HeartbeatTaskCommand, TaskLeaseDto>
{
    public async Task<TaskLeaseDto> HandleAsync(HeartbeatTaskCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await authorization.EnsureAsync(
            executionContext, command.TenantId, WmsPermissions.DocumentRead, ct: cancellationToken);
        var member = await caller.RequireAsync(command.TenantId, cancellationToken);

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        if (!await tasks.LockAsync(command.TenantId, command.TaskId, cancellationToken))
        {
            throw WmsNotFound.Task(command.TaskId);
        }

        var task = await tasks.GetAsync(command.TenantId, command.TaskId, cancellationToken)
            ?? throw WmsNotFound.Task(command.TaskId);
        await guard.EnsureCanPerformAsync(command.TenantId, task, cancellationToken);
        OperatorTaskGuard.EnsureActiveLease(task, member, command.LeaseToken);

        task.Heartbeat(member, command.LeaseToken, clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new TaskLeaseDto(task.LeaseExpiresAt!.Value);
    }
}
