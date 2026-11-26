using Microsoft.EntityFrameworkCore;
using Modules.Notifications.Application;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure.Persistence;

namespace Modules.Notifications.Infrastructure.InApp;

/// <summary>Spec 2026-11-22 §13: every query filters by <c>tenant_id</c> and <c>member_id</c> of the caller, with no exception.</summary>
internal sealed class InAppNotificationRepository(NotificationsDbContext dbContext) : IInAppNotificationRepository
{
    public async Task<IReadOnlyList<InAppNotification>> ListAsync(
        Guid tenantId,
        Guid memberId,
        bool unreadOnly,
        (DateTimeOffset CreatedAt, Guid Id)? after,
        int take,
        CancellationToken cancellationToken)
    {
        var query = dbContext.InAppNotifications
            .AsNoTracking()
            .Where(entry => entry.TenantId == tenantId && entry.MemberId == memberId);
        if (unreadOnly)
        {
            query = query.Where(entry => entry.ReadAt == null);
        }

        if (after is { } cursor)
        {
            // Row-value comparison: (created_at, id) < (cursor), the order of the list index.
            query = query.Where(entry => EF.Functions.LessThan(
                ValueTuple.Create(entry.CreatedAt, entry.Id), ValueTuple.Create(cursor.CreatedAt, cursor.Id)));
        }

        return await query
            .OrderByDescending(entry => entry.CreatedAt)
            .ThenByDescending(entry => entry.Id)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    public Task<int> CountUnreadAsync(Guid tenantId, Guid memberId, CancellationToken cancellationToken) =>
        dbContext.InAppNotifications.CountAsync(
            entry => entry.TenantId == tenantId && entry.MemberId == memberId && entry.ReadAt == null, cancellationToken);

    public async Task<InAppNotification?> MarkReadAsync(
        Guid tenantId, Guid memberId, Guid notificationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var entry = await dbContext.InAppNotifications.SingleOrDefaultAsync(
            candidate => candidate.Id == notificationId && candidate.TenantId == tenantId && candidate.MemberId == memberId,
            cancellationToken);
        if (entry is not null && entry.MarkRead(now))
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return entry;
    }

    public Task<int> MarkReadByResourceAsync(
        Guid tenantId, Guid memberId, InAppNotificationKind kind, Guid resourceId, DateTimeOffset now, CancellationToken cancellationToken) =>
        dbContext.InAppNotifications
            .Where(entry => entry.TenantId == tenantId && entry.MemberId == memberId
                && entry.Kind == kind && entry.ResourceId == resourceId && entry.ReadAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(entry => entry.ReadAt, now), cancellationToken);

    public Task MarkAllReadAsync(Guid tenantId, Guid memberId, DateTimeOffset? upTo, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var unread = dbContext.InAppNotifications
            .Where(entry => entry.TenantId == tenantId && entry.MemberId == memberId && entry.ReadAt == null);
        if (upTo is { } limit)
        {
            unread = unread.Where(entry => entry.CreatedAt <= limit);
        }

        return unread.ExecuteUpdateAsync(setters => setters.SetProperty(entry => entry.ReadAt, now), cancellationToken);
    }
}
