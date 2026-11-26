using BuildingBlocks.Application.Realtime;
using Microsoft.EntityFrameworkCore;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure.Persistence;

namespace Modules.Notifications.Infrastructure.InApp;

/// <summary>
/// Spec 2026-11-22 D-7: writes the entries of one outbox message and closes its inbox claim in a single transaction. Each entry is its own
/// <c>INSERT … ON CONFLICT DO NOTHING</c>, not a <c>SaveChanges</c>: a unique violation inside a batch of EF would revert the whole message
/// (a ticket alert carries one entry per recipient), and the message would then retry until <see cref="InAppOutboxConsumer.MaxAttempts"/>
/// without writing anything. Spec D-13: the entries a message closes (<see cref="InAppResolution"/>) go in the same transaction, each as
/// one <c>UPDATE … WHERE resolved_at IS NULL</c>, so two messages about the same order on two pods cannot both resolve it.
/// </summary>
internal static class InAppNotificationWriter
{
    public const string NotificationsChanged = "notificationsChanged";

    /// <returns>How many entries were new; the ones a repeated event already had are left as they were.</returns>
    public static Task<int> SaveAsync(
        NotificationsDbContext dbContext,
        string consumer,
        Guid messageId,
        IReadOnlyList<InAppNotification> entries,
        DateTimeOffset now,
        IRealtimeMemberPublisher publisher,
        CancellationToken cancellationToken) =>
        SaveAsync(dbContext, consumer, messageId, entries, [], now, publisher, cancellationToken, onInserted: null);

    /// <returns>How many entries were new; the ones a repeated event already had are left as they were.</returns>
    public static async Task<int> SaveAsync(
        NotificationsDbContext dbContext,
        string consumer,
        Guid messageId,
        IReadOnlyList<InAppNotification> entries,
        IReadOnlyList<InAppResolution> resolutions,
        DateTimeOffset now,
        IRealtimeMemberPublisher publisher,
        CancellationToken cancellationToken,
        Func<IReadOnlyList<InAppNotification>, Task>? onInserted = null)
    {
        var inserted = new List<InAppNotification>(entries.Count);
        var resolved = new List<ResolvedRow>();
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

            foreach (var resolution in resolutions)
            {
                resolved.AddRange(await ResolveAsync(dbContext, resolution, now));
            }

            // Inside the transaction and only for entries that really are new: a repeated message adds no second row of anything else.
            if (onInserted is not null && inserted.Count > 0)
            {
                await onInserted(inserted);
            }

            await dbContext.Database.ExecuteSqlAsync(
                $"""
                UPDATE notifications.inbox_messages SET processed_at = {now.ToUniversalTime()}
                WHERE consumer = {consumer} AND message_id = {messageId}
                """,
                CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        // After the commit: whoever rereads over HTTP must already find the entry. It never throws, and only what really changed is
        // announced.
        foreach (var entry in inserted)
        {
            await publisher.PublishToMemberAsync(
                entry.TenantId, entry.MemberId, NotificationsChanged, new { notificationId = entry.Id }, CancellationToken.None);
        }

        foreach (var row in resolved)
        {
            await publisher.PublishToMemberAsync(
                row.TenantId, row.MemberId, NotificationsChanged, new { notificationId = row.Id }, CancellationToken.None);
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

    // The same change as InAppNotification.Resolve (resolved and read now, the resolution into data), but atomic in the database: it only
    // touches what is still unresolved, and returns just those rows, so a repeated or concurrent message changes and announces nothing.
    private static async Task<List<ResolvedRow>> ResolveAsync(NotificationsDbContext dbContext, InAppResolution resolution, DateTimeOffset now)
    {
        var kind = resolution.Kind.ToString();
        var at = now.ToUniversalTime();
        // With a member, only that member's entry closes (task-notices spec D-2): a reassignment publishes the release of A and the
        // assignment of B with the same occurred_at, and B's new entry must stay open. Two statements, never an IS NULL on a parameter.
        if (resolution.MemberId is { } memberId)
        {
            return await dbContext.Database
                .SqlQuery<ResolvedRow>($"""
                    UPDATE notifications.in_app_notifications
                    SET resolved_at = {at},
                        read_at = COALESCE(read_at, {at}),
                        data = jsonb_set(data, ARRAY['resolution'], to_jsonb({resolution.Resolution}::text))
                    WHERE tenant_id = {resolution.TenantId} AND kind = {kind} AND resource_id = {resolution.ResourceId}
                        AND member_id = {memberId} AND resolved_at IS NULL
                    RETURNING id AS "Id", tenant_id AS "TenantId", member_id AS "MemberId"
                    """)
                .ToListAsync(CancellationToken.None);
        }

        return await dbContext.Database
            .SqlQuery<ResolvedRow>($"""
                UPDATE notifications.in_app_notifications
                SET resolved_at = {at},
                    read_at = COALESCE(read_at, {at}),
                    data = jsonb_set(data, ARRAY['resolution'], to_jsonb({resolution.Resolution}::text))
                WHERE tenant_id = {resolution.TenantId} AND kind = {kind} AND resource_id = {resolution.ResourceId} AND resolved_at IS NULL
                RETURNING id AS "Id", tenant_id AS "TenantId", member_id AS "MemberId"
                """)
            .ToListAsync(CancellationToken.None);
    }

    private sealed record ResolvedRow(Guid Id, Guid TenantId, Guid MemberId);
}
