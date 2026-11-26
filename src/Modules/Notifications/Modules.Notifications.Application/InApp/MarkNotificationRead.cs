using BuildingBlocks.Application;
using BuildingBlocks.Application.Realtime;
using Modules.Tenancy.Application;

namespace Modules.Notifications.Application;

public sealed record MarkNotificationReadCommand(Guid TenantId, Guid NotificationId) : ICommand<InAppNotificationReadDto>;

/// <summary>
/// Spec 2026-11-22 §8.3: idempotent, with no <c>If-Match</c> (a personal mark with no race to lose: two tabs marking the same one end up
/// alike). An id that is not the caller's, of another member or nonexistent, is 404. After saving, the member's other tabs are told so
/// their badge updates; the notice never throws.
/// </summary>
public sealed class MarkNotificationReadHandler(
    IExecutionContext executionContext,
    IMembershipDirectory memberships,
    IInAppNotificationRepository repository,
    IInAppNotificationNames names,
    IRealtimeMemberPublisher publisher,
    IClock clock)
    : ICommandHandler<MarkNotificationReadCommand, InAppNotificationReadDto>
{
    public async Task<InAppNotificationReadDto> HandleAsync(MarkNotificationReadCommand command, CancellationToken cancellationToken)
    {
        var memberId = await InAppAuthorization.EnsureMemberAsync(executionContext, memberships, command.TenantId, cancellationToken);
        // Microseconds, as the database keeps them: the DTO is built from the tracked entity and must equal what a re-read returns.
        var now = InAppTime.ToMicroseconds(clock.UtcNow);
        var entry = await repository.MarkReadAsync(command.TenantId, memberId, command.NotificationId, now, cancellationToken)
            ?? throw new ResourceNotFoundException(NotificationsErrorCodes.NotificationNotFound, "The notification does not exist.");

        await publisher.PublishToMemberAsync(
            command.TenantId, memberId, InAppNotificationEvents.NotificationsChanged, new { notificationId = entry.Id }, CancellationToken.None);

        var dto = (await InAppNotificationMapper.ToDtosAsync([entry], command.TenantId, names, now, cancellationToken))[0];
        var unread = await repository.CountUnreadAsync(command.TenantId, memberId, cancellationToken);
        return new InAppNotificationReadDto(dto, unread);
    }
}

/// <summary>The kind of the notice every write sends to the member's group (spec §9.1).</summary>
public static class InAppNotificationEvents
{
    public const string NotificationsChanged = "notificationsChanged";
}
