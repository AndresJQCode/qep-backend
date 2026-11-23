using BuildingBlocks.Application;
using BuildingBlocks.Application.Realtime;
using FluentValidation;
using Modules.Notifications.Domain;
using Modules.Tenancy.Application;

namespace Modules.Notifications.Application;

public sealed record MarkResourceNotificationsReadCommand(Guid TenantId, string Kind, Guid ResourceId) : ICommand<MarkResourceReadResultDto>;

public sealed class MarkResourceNotificationsReadValidator : AbstractValidator<MarkResourceNotificationsReadCommand>
{
    public MarkResourceNotificationsReadValidator()
    {
        // Exact name only: Enum.TryParse would also accept "1" and any casing, and the screen sends the name it was served.
        RuleFor(command => command.Kind)
            .Must(kind => kind is not null && Enum.GetNames<InAppNotificationKind>().Contains(kind, StringComparer.Ordinal))
            .OverridePropertyName("kind")
            .WithMessage("Indica un tipo de aviso válido.");
        RuleFor(command => command.ResourceId)
            .NotEmpty()
            .OverridePropertyName("resourceId")
            .WithMessage("Indica el recurso del aviso.");
    }
}

/// <summary>
/// Task-notices spec 2026-11-22 D-2: marks read the caller's unread entries of one kind about one resource (opening a ticket reads its
/// «te asignaron» entry, whatever path led there). Idempotent and never 404: a ticket with no entries marks nothing. It only touches the
/// caller's own entries and only tells the member's tabs when something really changed.
/// </summary>
public sealed class MarkResourceNotificationsReadHandler(
    IExecutionContext executionContext,
    IMembershipDirectory memberships,
    IInAppNotificationRepository repository,
    IRealtimeMemberPublisher publisher,
    IClock clock,
    IValidator<MarkResourceNotificationsReadCommand> validator)
    : ICommandHandler<MarkResourceNotificationsReadCommand, MarkResourceReadResultDto>
{
    public async Task<MarkResourceReadResultDto> HandleAsync(MarkResourceNotificationsReadCommand command, CancellationToken cancellationToken)
    {
        var memberId = await InAppAuthorization.EnsureMemberAsync(executionContext, memberships, command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var kind = Enum.Parse<InAppNotificationKind>(command.Kind);
        var marked = await repository.MarkReadByResourceAsync(
            command.TenantId, memberId, kind, command.ResourceId, InAppTime.ToMicroseconds(clock.UtcNow), cancellationToken);
        if (marked > 0)
        {
            // A null notificationId: not one entry but several changed.
            await publisher.PublishToMemberAsync(
                command.TenantId, memberId, InAppNotificationEvents.NotificationsChanged, new { notificationId = (Guid?)null }, CancellationToken.None);
        }

        return new MarkResourceReadResultDto(marked, await repository.CountUnreadAsync(command.TenantId, memberId, cancellationToken));
    }
}
