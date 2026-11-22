using BuildingBlocks.Application.Realtime;
using Microsoft.EntityFrameworkCore;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure.Persistence;

namespace Modules.Notifications.Infrastructure.InApp;

/// <summary>
/// Spec 2026-11-22 D-7: writes the entries of one outbox message and closes its inbox claim in a single transaction. Each entry is its own
/// <c>INSERT … ON CONFLICT DO NOTHING</c>, not a <c>SaveChanges</c>: a unique violation inside a batch of EF would revert the whole message
/// (a ticket alert carries one entry per recipient), and the message would then retry until <see cref="InAppOutboxConsumer.MaxAttempts"/>
/// without writing anything.
/// </summary>
internal static class InAppNotificationWriter
{
    public const string NotificationsChanged = "notificationsChanged";

    /// <returns>How many entries were new; the ones a repeated event already had are left as they were.</returns>
    public static async Task<int> SaveAsync(
        NotificationsDbContext dbContext,
        string consumer,
        Guid messageId,
        IReadOnlyList<InAppNotification> entries,
        DateTimeOffset now,
        IRealtimeMemberPublisher publisher,
        CancellationToken cancellationToken)
    {
        var inserted = new List<InAppNotification>(entries.Count);
        // Closing the claim is not an act of the caller: from here the entries are decided, so the shutdown does not interrupt it.
        await using (var transaction = await dbContext.Database.BeginTransactionAsync(CancellationToken.None))
        {
            foreach (var entry in entries)
            {
                if (await InsertAsync(dbContext, entry))
                {
                    inserted.Add(entry);
                }
            }

            await dbContext.Database.ExecuteSqlAsync(
                $"""
                UPDATE notifications.inbox_messages SET processed_at = {now.ToUniversalTime()}
                WHERE consumer = {consumer} AND message_id = {messageId}
                """,
                CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        // After the commit: whoever rereads over HTTP must already find the entry. It never throws, and only what really was inserted
        // is announced.
        foreach (var entry in inserted)
        {
            await publisher.PublishToMemberAsync(
                entry.TenantId, entry.MemberId, NotificationsChanged, new { notificationId = entry.Id }, CancellationToken.None);
        }

        return inserted.Count;
    }

    private static async Task<bool> InsertAsync(NotificationsDbContext dbContext, InAppNotification entry)
    {
        var kind = entry.Kind.ToString();
        var created = entry.CreatedAt.ToUniversalTime();
        var ids = await dbContext.Database
            .SqlQuery<Guid>($"""
                INSERT INTO notifications.in_app_notifications
                    (id, tenant_id, member_id, kind, resource_id, data, dedupe_key, source_message_id, created_at)
                VALUES ({entry.Id}, {entry.TenantId}, {entry.MemberId}, {kind}, {entry.ResourceId}, CAST({entry.DataJson} AS jsonb),
                        {entry.DedupeKey}, {entry.SourceMessageId}, {created})
                ON CONFLICT (tenant_id, member_id, dedupe_key) DO NOTHING
                RETURNING id AS "Value"
                """)
            // No FirstOrDefaultAsync: composing over an INSERT … RETURNING wraps it in a SELECT, which Postgres rejects (same as InboxClaims).
            .ToListAsync(CancellationToken.None);
        return ids.Count == 1;
    }
}
