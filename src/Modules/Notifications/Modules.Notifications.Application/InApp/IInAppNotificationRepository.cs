using Modules.Notifications.Domain;

namespace Modules.Notifications.Application;

/// <summary>
/// Spec 2026-11-22 §8 and §13: every method filters by <c>tenant_id</c> <b>and</b> <c>member_id</c> of the caller, with no exception. An
/// entry of another member of the same tenant is simply not found.
/// </summary>
public interface IInAppNotificationRepository
{
    /// <summary>Newest first (created, then id). <paramref name="after"/> is the last entry of the previous page. Asks for what it is told:
    /// the handler passes one more than the page to know whether there is a next one.</summary>
    Task<IReadOnlyList<InAppNotification>> ListAsync(
        Guid tenantId,
        Guid memberId,
        bool unreadOnly,
        (DateTimeOffset CreatedAt, Guid Id)? after,
        int take,
        CancellationToken cancellationToken);

    Task<int> CountUnreadAsync(Guid tenantId, Guid memberId, CancellationToken cancellationToken);

    /// <returns>The entry marked read (the first <c>ReadAt</c> stays when it already was), or <c>null</c> when it is not the caller's.</returns>
    Task<InAppNotification?> MarkReadAsync(Guid tenantId, Guid memberId, Guid notificationId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Marks the caller's unread entries of <paramref name="kind"/> about <paramref name="resourceId"/> (task-notices spec D-2).</summary>
    /// <returns>How many it marked; zero when there were none or all were already read.</returns>
    Task<int> MarkReadByResourceAsync(
        Guid tenantId, Guid memberId, InAppNotificationKind kind, Guid resourceId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Marks the unread ones created at or before <paramref name="upTo"/>, or every unread one when it is <c>null</c>.</summary>
    Task MarkAllReadAsync(Guid tenantId, Guid memberId, DateTimeOffset? upTo, DateTimeOffset now, CancellationToken cancellationToken);
}
