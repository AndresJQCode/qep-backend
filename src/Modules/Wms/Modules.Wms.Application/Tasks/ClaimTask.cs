using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;
using Modules.Wms.Domain;
using Modules.Wms.Domain.Tasks;

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

/// <summary>Reclama una tarea concreta, desde «Mis tareas». Reclamar la que ya tiene en curso la devuelve con el lease renovado.</summary>
public sealed class ClaimTaskHandler(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    WmsCaller caller,
    IWmsUnitOfWork unitOfWork,
    IWarehouseTaskRepository tasks,
    OperatorTaskGuard guard,
    IClock clock,
    WarehouseTaskDtoComposer composer,
    IIncidentRepository incidents,
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
        await CountSelfVerificationRule.EnsureNotReporterAsync(incidents, command.TenantId, task, member, cancellationToken);

        // Antes de reclamar: así el 409 dice cuál es la otra. El índice único es la última red, y no lo sabe.
        var current = await tasks.FindInProgressOfMemberAsync(command.TenantId, member, cancellationToken);
        if (current is not null)
        {
            if (current.Id != task.Id)
            {
                throw new TaskMemberBusyException(current.Id);
            }

            // M2: reclamar la que ya tiene en curso renueva el lease con el mismo token, como el latido.
            task.RenewClaim(member, clock.UtcNow);
            var renewed = await composer.ComposeAsync(task, member, withEvents: false, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return renewed;
        }

        // M3: por id sólo se reclama lo que se le ofreció a quien llama. El trabajo nuevo del pool llega por «siguiente».
        if (!IsOfferedTo(task, member))
        {
            throw new WmsDomainException(WmsErrorCodes.TaskNotOffered, "Esta tarea no se te ha ofrecido: pide la siguiente tarea.");
        }

        task.Claim(member, command.DeviceId, clock.UtcNow);
        var dto = await composer.ComposeAsync(task, member, withEvents: false, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return dto;
    }

    /// <summary>Una tarea del pool sólo se ofrece a quien la tenía reclamada y el barrido se la devolvió. La asignada a otra persona
    /// sigue siendo asunto de <see cref="WarehouseTask.Claim"/>, que responde con su propio error.</summary>
    private static bool IsOfferedTo(WarehouseTask task, Guid member) =>
        task.Status != WarehouseTaskStatus.Ready || (task.ClaimedByMemberId == member && task.LeaseToken is not null);
}
