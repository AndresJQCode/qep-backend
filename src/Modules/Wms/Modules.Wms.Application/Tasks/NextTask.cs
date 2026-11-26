using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;

namespace Modules.Wms.Application;

public sealed record NextTaskCommand(Guid TenantId, Guid WarehouseId, string DeviceId) : ICommand<NextTaskResultDto>
{
    public static NextTaskCommand From(Guid tenantId, NextTaskRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(tenantId, request.WarehouseId, request.DeviceId ?? string.Empty);
    }
}

public sealed class NextTaskValidator : AbstractValidator<NextTaskCommand>
{
    public NextTaskValidator() => RuleFor(command => command.DeviceId).MustBeADeviceId();
}

/// <summary>«Siguiente tarea» (spec 2026-11-26 §6.8): la mejor elegible, ya reclamada. Repetirla devuelve la que tiene en curso.
/// </summary>
public sealed class NextTaskHandler(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    WmsCaller caller,
    IWmsUnitOfWork unitOfWork,
    IWarehouseTaskRepository tasks,
    IClock clock,
    WarehouseTaskDtoComposer composer,
    IValidator<NextTaskCommand> validator)
    : ICommandHandler<NextTaskCommand, NextTaskResultDto>
{
    public async Task<NextTaskResultDto> HandleAsync(NextTaskCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await authorization.EnsureAsync(
            executionContext, command.TenantId, WmsPermissions.DocumentRead, warehouseId: command.WarehouseId, ct: cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var types = TaskTypePermissions.AllowedFor(executionContext);
        if (types.Count == 0)
        {
            throw WmsAuthorization.Denied();
        }

        var member = await caller.RequireAsync(command.TenantId, cancellationToken);

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        await unitOfWork.LockAsync(TaskMemberLocks.For(member), cancellationToken);

        var current = await tasks.FindInProgressOfMemberAsync(command.TenantId, member, cancellationToken);
        if (current is not null)
        {
            if (current.WarehouseId.Value != command.WarehouseId)
            {
                throw new TaskMemberBusyException(current.Id);
            }

            return new NextTaskResultDto(
                await composer.ComposeAsync(current, member, withEvents: false, cancellationToken));
        }

        var now = clock.UtcNow;
        var candidate = await tasks.LockNextCandidateAsync(
            command.TenantId, command.WarehouseId, member, types, now, cancellationToken);
        if (candidate is not { } candidateId)
        {
            return new NextTaskResultDto(null);
        }

        // La fila ya está bloqueada por esta transacción (FOR UPDATE SKIP LOCKED).
        var task = await tasks.GetAsync(command.TenantId, candidateId, cancellationToken)
            ?? throw WmsNotFound.Task(candidateId);
        task.Claim(member, command.DeviceId, now);
        var dto = await composer.ComposeAsync(task, member, withEvents: false, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new NextTaskResultDto(dto);
    }
}
