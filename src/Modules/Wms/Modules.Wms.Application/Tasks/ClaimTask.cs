using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;

namespace Modules.Wms.Application;

public sealed record ClaimTaskCommand(Guid TenantId, Guid TaskId, string DeviceId) : ICommand<TaskDto>
{
    public static ClaimTaskCommand From(Guid tenantId, Guid taskId, ClaimTaskRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(tenantId, taskId, request.DeviceId ?? string.Empty);
    }
}

public sealed class ClaimTaskValidator : AbstractValidator<ClaimTaskCommand>
{
    public ClaimTaskValidator() => RuleFor(command => command.DeviceId).MustBeADeviceId();
}

/// <summary>Reclama una tarea concreta, desde «Mis tareas». Reclamar la que ya tiene en curso la devuelve sin cambios.</summary>
public sealed class ClaimTaskHandler(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    WmsCaller caller,
    IWmsUnitOfWork unitOfWork,
    IWarehouseTaskRepository tasks,
    OperatorTaskGuard guard,
    IClock clock,
    WarehouseTaskDtoComposer composer,
    IValidator<ClaimTaskCommand> validator)
    : ICommandHandler<ClaimTaskCommand, TaskDto>
{
    public async Task<TaskDto> HandleAsync(ClaimTaskCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await authorization.EnsureAsync(
            executionContext, command.TenantId, WmsPermissions.DocumentRead, ct: cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var member = await caller.RequireAsync(command.TenantId, cancellationToken);

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        await unitOfWork.LockAsync(TaskMemberLocks.For(member), cancellationToken);
        if (!await tasks.LockAsync(command.TenantId, command.TaskId, cancellationToken))
        {
            throw WmsNotFound.Task(command.TaskId);
        }

        var task = await tasks.GetAsync(command.TenantId, command.TaskId, cancellationToken)
            ?? throw WmsNotFound.Task(command.TaskId);
        await guard.EnsureCanPerformAsync(command.TenantId, task, cancellationToken);

        // Antes de reclamar: así el 409 dice cuál es la otra. El índice único es la última red, y no lo sabe.
        var current = await tasks.FindInProgressOfMemberAsync(command.TenantId, member, cancellationToken);
        if (current is not null)
        {
            return current.Id == task.Id
                ? await composer.ComposeAsync(task, member, withEvents: false, cancellationToken)
                : throw new TaskMemberBusyException(current.Id);
        }

        task.Claim(member, command.DeviceId, clock.UtcNow);
        var dto = await composer.ComposeAsync(task, member, withEvents: false, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return dto;
    }
}
