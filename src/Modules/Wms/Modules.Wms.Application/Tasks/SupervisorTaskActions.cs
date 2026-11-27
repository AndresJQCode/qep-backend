using System.Globalization;
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;
using Modules.Wms.Domain;
using Modules.Wms.Domain.Tasks;
using static Modules.Wms.Application.WmsAuditActions;

namespace Modules.Wms.Application;

/// <summary>
/// El molde de las acciones del supervisor: autoriza → bloquea la fila → carga (404) → autoriza con dueño y bodega → versión (412) →
/// muta → avisa el cierre → audita → compone → guarda. El bloqueo va antes de cargar, como en las rutas del operario: así una acción
/// nunca se cruza con una confirmación a medias (la tarea siempre se bloquea antes que sus líneas y que los saldos) y la versión se
/// compara con la fila vigente.
/// </summary>
public sealed class SupervisorTaskRunner(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    IWarehouseTaskRepository tasks,
    IWmsUnitOfWork unitOfWork,
    IWmsAuditRecorder auditRecorder,
    IClock clock,
    WmsCaller caller,
    WarehouseTaskDtoComposer composer,
    TaskClosedNotifier closed)
{
    public Task AuthorizeAsync(Guid tenantId, CancellationToken cancellationToken) =>
        authorization.EnsureAsync(executionContext, tenantId, WmsPermissions.TaskManage, ct: cancellationToken);

    /// <param name="mutate">Recibe la tarea, la membresía que actúa y la hora.</param>
    /// <param name="fields">Lo propio de la acción para la auditoría; el dueño y el número se agregan acá.</param>
    /// <param name="precondition">Lo que haya que consultar antes de mutar, ya con la tarea bloqueada.</param>
    public async Task<TaskDto> RunAsync(
        Guid tenantId,
        Guid taskId,
        long version,
        string auditAction,
        Action<WarehouseTask, Guid, DateTimeOffset> mutate,
        IReadOnlyDictionary<string, string?> fields,
        CancellationToken cancellationToken,
        Func<WarehouseTask, Task>? precondition = null)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        ArgumentNullException.ThrowIfNull(fields);
        await AuthorizeAsync(tenantId, cancellationToken);
        var actor = await caller.RequireAsync(tenantId, cancellationToken);

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        if (!await tasks.LockAsync(tenantId, taskId, cancellationToken))
        {
            throw WmsNotFound.Task(taskId);
        }

        var task = await tasks.GetAsync(tenantId, taskId, cancellationToken) ?? throw WmsNotFound.Task(taskId);
        await authorization.EnsureAsync(
            executionContext, tenantId, WmsPermissions.TaskManage, task.OwnerId.Value, task.WarehouseId.Value, cancellationToken);
        WmsVersions.Ensure(task.Version, version);
        if (precondition is not null)
        {
            await precondition(task);
        }

        var now = clock.UtcNow;
        var before = task.Status;
        mutate(task, actor, now);
        await closed.NotifyIfClosedAsync(before, new TaskClosed(task, actor, null, now, now), cancellationToken);

        var changed = new Dictionary<string, string?>(fields)
        {
            ["ownerId"] = task.OwnerId.Value.ToString(),
            ["number"] = task.Number,
        };
        auditRecorder.Record(
            tenantId, executionContext.SubjectId, WmsActorType.Human, auditAction, Resources.Task, task.Id.ToString(), changed, now);

        var dto = await composer.ComposeAsync(task, viewerMemberId: null, withEvents: false, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return dto;
    }

    internal static Dictionary<string, string?> ReasonFields(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? [] : new() { ["reason"] = reason.Trim() };
}

internal static class TaskReasonRules
{
    public static IRuleBuilderOptions<T, string?> MustFitATaskReason<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(reason => reason is null || reason.Trim().Length <= WarehouseTask.ReasonMaxLength)
            .WithMessage($"The reason has at most {WarehouseTask.ReasonMaxLength} characters.");

    public static IRuleBuilderOptions<T, string> MustBeARequiredTaskReason<T>(this IRuleBuilder<T, string> rule) =>
        rule.Must(reason => !string.IsNullOrWhiteSpace(reason) && reason.Trim().Length <= WarehouseTask.ReasonMaxLength)
            .WithMessage($"The reason is required and has at most {WarehouseTask.ReasonMaxLength} characters.");
}

public sealed record PublishTaskCommand(Guid TenantId, Guid TaskId, long Version) : ICommand<TaskDto>;

public sealed class PublishTaskHandler(SupervisorTaskRunner runner) : ICommandHandler<PublishTaskCommand, TaskDto>
{
    public Task<TaskDto> HandleAsync(PublishTaskCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return runner.RunAsync(
            command.TenantId,
            command.TaskId,
            command.Version,
            TaskPublished,
            (task, actor, now) => task.Publish(actor, now),
            new Dictionary<string, string?>(),
            cancellationToken);
    }
}

public sealed record HoldTaskCommand(Guid TenantId, Guid TaskId, long Version, string? Reason) : ICommand<TaskDto>;

public sealed class HoldTaskValidator : AbstractValidator<HoldTaskCommand>
{
    public HoldTaskValidator() => RuleFor(command => command.Reason).MustFitATaskReason();
}

public sealed class HoldTaskHandler(SupervisorTaskRunner runner, IValidator<HoldTaskCommand> validator)
    : ICommandHandler<HoldTaskCommand, TaskDto>
{
    public async Task<TaskDto> HandleAsync(HoldTaskCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await runner.AuthorizeAsync(command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        return await runner.RunAsync(
            command.TenantId,
            command.TaskId,
            command.Version,
            TaskHeld,
            (task, actor, now) => task.Hold(actor, command.Reason, now),
            SupervisorTaskRunner.ReasonFields(command.Reason),
            cancellationToken);
    }
}

public sealed record AssignTaskCommand(Guid TenantId, Guid TaskId, Guid MemberId, long Version, string? Reason) : ICommand<TaskDto>;

/// <summary>Que el motivo sea obligatorio al reasignar una tarea en curso lo decide el dominio, que ve el estado.</summary>
public sealed class AssignTaskValidator : AbstractValidator<AssignTaskCommand>
{
    public AssignTaskValidator()
    {
        RuleFor(command => command.MemberId).NotEmpty();
        RuleFor(command => command.Reason).MustFitATaskReason();
    }
}

/// <summary>Asignar, o reasignar una tarea en curso, que revoca el lease de quien la tiene (spec §6.5).</summary>
public sealed class AssignTaskHandler(
    SupervisorTaskRunner runner, IWmsMemberDirectory members, IValidator<AssignTaskCommand> validator)
    : ICommandHandler<AssignTaskCommand, TaskDto>
{
    public async Task<TaskDto> HandleAsync(AssignTaskCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await runner.AuthorizeAsync(command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var fields = SupervisorTaskRunner.ReasonFields(command.Reason);
        fields["assignedMemberId"] = command.MemberId.ToString();
        return await runner.RunAsync(
            command.TenantId,
            command.TaskId,
            command.Version,
            TaskAssigned,
            (task, actor, now) => task.Assign(command.MemberId, actor, command.Reason, now),
            fields,
            cancellationToken,
            async task =>
            {
                if (!await members.CanPerformAsync(
                    command.TenantId, command.MemberId, TaskTypePermissions.For(task.Type), cancellationToken))
                {
                    throw new WmsDomainException(
                        WmsErrorCodes.TaskAssigneeNotEligible,
                        "Esa persona no está activa o no tiene permiso para hacer esta tarea.");
                }
            });
    }
}

public sealed record UnassignTaskCommand(Guid TenantId, Guid TaskId, long Version) : ICommand<TaskDto>;

public sealed class UnassignTaskHandler(SupervisorTaskRunner runner) : ICommandHandler<UnassignTaskCommand, TaskDto>
{
    public Task<TaskDto> HandleAsync(UnassignTaskCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return runner.RunAsync(
            command.TenantId,
            command.TaskId,
            command.Version,
            TaskUnassigned,
            (task, actor, now) => task.Unassign(actor, now),
            new Dictionary<string, string?>(),
            cancellationToken);
    }
}

public sealed record ChangeTaskPriorityCommand(Guid TenantId, Guid TaskId, int Priority, bool Urgent, long Version)
    : ICommand<TaskDto>;

public sealed class ChangeTaskPriorityValidator : AbstractValidator<ChangeTaskPriorityCommand>
{
    public ChangeTaskPriorityValidator() =>
        RuleFor(command => command.Priority).InclusiveBetween(WarehouseTask.MinPriority, WarehouseTask.MaxPriority);
}

public sealed class ChangeTaskPriorityHandler(SupervisorTaskRunner runner, IValidator<ChangeTaskPriorityCommand> validator)
    : ICommandHandler<ChangeTaskPriorityCommand, TaskDto>
{
    public async Task<TaskDto> HandleAsync(ChangeTaskPriorityCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await runner.AuthorizeAsync(command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        return await runner.RunAsync(
            command.TenantId,
            command.TaskId,
            command.Version,
            TaskPriorityChanged,
            (task, actor, now) => task.ChangePriority(command.Priority, command.Urgent, actor, now),
            new Dictionary<string, string?>
            {
                ["priority"] = command.Priority.ToString(CultureInfo.InvariantCulture),
                ["urgent"] = command.Urgent ? "true" : "false",
            },
            cancellationToken);
    }
}

public sealed record CancelTaskCommand(Guid TenantId, Guid TaskId, string Reason, long Version) : ICommand<TaskDto>
{
    // Un motivo ausente llega null: se vuelve vacío para que lo atrape el validador.
    public static CancelTaskCommand From(Guid tenantId, Guid taskId, CancelTaskRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(tenantId, taskId, request.Reason ?? string.Empty, request.Version);
    }
}

public sealed class CancelTaskValidator : AbstractValidator<CancelTaskCommand>
{
    public CancelTaskValidator() => RuleFor(command => command.Reason).MustBeARequiredTaskReason();
}

public sealed class CancelTaskHandler(SupervisorTaskRunner runner, IValidator<CancelTaskCommand> validator)
    : ICommandHandler<CancelTaskCommand, TaskDto>
{
    public async Task<TaskDto> HandleAsync(CancelTaskCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await runner.AuthorizeAsync(command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        return await runner.RunAsync(
            command.TenantId,
            command.TaskId,
            command.Version,
            TaskCancelled,
            (task, actor, now) => task.Cancel(command.Reason, actor, now),
            SupervisorTaskRunner.ReasonFields(command.Reason),
            cancellationToken);
    }
}

public sealed record CancelTaskLineCommand(Guid TenantId, Guid TaskId, Guid LineId, string Reason, long Version) : ICommand<TaskDto>
{
    public static CancelTaskLineCommand From(Guid tenantId, Guid taskId, Guid lineId, CancelTaskRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(tenantId, taskId, lineId, request.Reason ?? string.Empty, request.Version);
    }
}

public sealed class CancelTaskLineValidator : AbstractValidator<CancelTaskLineCommand>
{
    public CancelTaskLineValidator() => RuleFor(command => command.Reason).MustBeARequiredTaskReason();
}

public sealed class CancelTaskLineHandler(SupervisorTaskRunner runner, IValidator<CancelTaskLineCommand> validator)
    : ICommandHandler<CancelTaskLineCommand, TaskDto>
{
    public async Task<TaskDto> HandleAsync(CancelTaskLineCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await runner.AuthorizeAsync(command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var fields = SupervisorTaskRunner.ReasonFields(command.Reason);
        fields["lineId"] = command.LineId.ToString();
        return await runner.RunAsync(
            command.TenantId,
            command.TaskId,
            command.Version,
            TaskLineCancelled,
            (task, actor, now) =>
            {
                // El dominio trata una línea ajena como error de cableado: el 404 sale de acá.
                if (task.FindLine(command.LineId) is null)
                {
                    throw WmsNotFound.TaskLine(command.LineId);
                }

                task.CancelLine(command.LineId, command.Reason, actor, now);
            },
            fields,
            cancellationToken);
    }
}
