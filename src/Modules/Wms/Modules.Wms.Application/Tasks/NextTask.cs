using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;
using Modules.Wms.Domain.Tasks;

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

/// <summary>«Siguiente tarea» (spec 2026-11-26 §6.8): la mejor elegible, ya reclamada. Repetirla devuelve la que tiene en curso, con
/// el lease renovado.
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

            // M2: la fila se bloquea y se recarga antes de renovar (orden: operario → tarea). Si el barrido la venció en el medio y
            // sigue siendo suya (reclamo y token intactos, y es de las que su rol puede hacer), se la vuelve a reclamar: en el mismo
            // celular conserva el token (DA-34). Si ya no es suya (un supervisor la retuvo o la reasignó), sigue el camino normal.
            if (await tasks.LockAsync(command.TenantId, current.Id, cancellationToken)
                && await tasks.GetAsync(command.TenantId, current.Id, cancellationToken) is { } mine
                && mine.ClaimedByMemberId == member
                && types.Contains(mine.Type))
            {
                var swept = mine.Status == WarehouseTaskStatus.Ready
                    || (mine.Status == WarehouseTaskStatus.Assigned && mine.AssignedMemberId == member);
                if (mine.Status == WarehouseTaskStatus.InProgress)
                {
                    mine.RenewClaim(member, command.DeviceId, clock.UtcNow);
                }
                else if (swept && mine.LeaseToken is not null)
                {
                    mine.Claim(member, command.DeviceId, clock.UtcNow);
                }

                if (mine.Status == WarehouseTaskStatus.InProgress)
                {
                    var own = await composer.ComposeAsync(mine, member, withEvents: false, cancellationToken);
                    await unitOfWork.SaveChangesAsync(cancellationToken);
                    return new NextTaskResultDto(own);
                }
            }
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
