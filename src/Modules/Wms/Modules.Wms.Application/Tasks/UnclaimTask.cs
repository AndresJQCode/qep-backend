using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;
using Modules.Wms.Domain.Tasks;

namespace Modules.Wms.Application;

public sealed record UnclaimTaskCommand(Guid TenantId, Guid TaskId, Guid LeaseToken, string? Reason) : ICommand<TaskDto>;

public sealed class UnclaimTaskValidator : AbstractValidator<UnclaimTaskCommand>
{
    public UnclaimTaskValidator() =>
        RuleFor(command => command.Reason)
            .Must(reason => Enum.GetNames<TaskUnclaimReason>().Contains(reason, StringComparer.Ordinal))
            .WithMessage($"The reason must be one of: {string.Join(", ", Enum.GetNames<TaskUnclaimReason>())}.");
}

/// <summary>Soltar: la tarea vuelve al pool con su motivo, que se mide (spec 2026-11-26 §6.7).</summary>
public sealed class UnclaimTaskHandler(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    WmsCaller caller,
    IWmsUnitOfWork unitOfWork,
    IWarehouseTaskRepository tasks,
    OperatorTaskGuard guard,
    IClock clock,
    WarehouseTaskDtoComposer composer,
    IValidator<UnclaimTaskCommand> validator)
    : ICommandHandler<UnclaimTaskCommand, TaskDto>
{
    public async Task<TaskDto> HandleAsync(UnclaimTaskCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await authorization.EnsureAsync(
            executionContext, command.TenantId, WmsPermissions.DocumentRead, ct: cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
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

        task.Unclaim(member, command.LeaseToken, Enum.Parse<TaskUnclaimReason>(command.Reason!), clock.UtcNow);
        var dto = await composer.ComposeAsync(task, member, withEvents: false, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return dto;
    }
}
