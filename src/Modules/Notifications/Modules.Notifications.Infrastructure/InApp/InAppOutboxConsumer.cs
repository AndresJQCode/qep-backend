using BuildingBlocks.Application;
using BuildingBlocks.Application.Realtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure.Persistence;
using Modules.Tenancy.Application;

namespace Modules.Notifications.Infrastructure.InApp;

/// <summary>
/// Spec 2026-11-22 §12: what the bell's consumers share. Like <see cref="Messaging.OutboxDeliveryWorker"/> it is a singleton definition
/// drained in sequence by <see cref="Messaging.NotificationsDeliveryWorker"/>, with the same claim (<see cref="InboxClaims"/>, a lease per
/// consumer and message) and the same per-message isolation; instead of an email it writes <see cref="InAppNotification"/> entries.
/// A message that builds no entries (a recipient without membership, a manual order) is still processed.
/// </summary>
internal abstract partial class InAppOutboxConsumer(IServiceScopeFactory scopeFactory, ILogger logger)
{
    internal const int BatchSize = 20;

    /// <summary>Claims that end unfinished are tolerated this many times; the next one records the message as processed with no entry.</summary>
    internal const int MaxAttempts = 3;

    internal static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    /// <summary>Spec D-6: the outbox already holds history, and a consumer born today must not fill every bell with it. What is older is
    /// never claimed, so it does not even cost an inbox row.</summary>
    internal static readonly TimeSpan MaxEventAge = TimeSpan.FromHours(24);

    protected abstract string Consumer { get; }

    protected abstract IReadOnlyCollection<string> EventNames { get; }

    internal string ConsumerName => Consumer;

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Outbox message {MessageId} failed in {Consumer}; it is retried when its lease expires.")]
    private static partial void LogMessageFailed(ILogger logger, Guid messageId, string consumer, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Outbox message {MessageId} was claimed {Attempts} times by {Consumer} without finishing; it is recorded as processed with no entry.")]
    private static partial void LogAttemptsExhausted(ILogger logger, Guid messageId, int attempts, string consumer);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Outbox message {MessageId} created {Count} in-app entries in {Consumer}.")]
    private static partial void LogEntriesCreated(ILogger logger, Guid messageId, int count, string consumer);

    /// <summary>The entries of one message, from its payload. A throw leaves the claim alive and the message is retried when the lease
    /// expires; empty is a decision (nobody to notify), not a failure.</summary>
    protected abstract Task<IReadOnlyList<InAppNotification>> BuildAsync(
        OutboxRecord record, InAppContext context, CancellationToken cancellationToken);

    /// <summary>The entries this message closes instead of opening (a wholesale order that was approved or cancelled, D-13). They are written
    /// in the same transaction as the new entries and the processed mark. Most consumers close nothing.</summary>
    protected virtual Task<IReadOnlyList<InAppResolution>> ResolutionsAsync(
        OutboxRecord record, InAppContext context, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<InAppResolution>>([]);

    /// <summary>Runs inside the write transaction, after the entries are inserted and before the processed mark, with only the entries that
    /// really were new. For rows that must exist exactly when an entry does (the task-notice escalation); most consumers add none.</summary>
    protected virtual Task OnInsertedAsync(
        NotificationsDbContext dbContext,
        OutboxRecord record,
        IReadOnlyList<InAppNotification> inserted,
        DateTimeOffset now,
        CancellationToken cancellationToken) => Task.CompletedTask;

    // Called by the loop of NotificationsDeliveryWorker and, directly, by the tests (InternalsVisibleTo). Virtual so the loop tests can
    // count drains without a database. Returns true when the batch came full: there may be more.
    internal virtual async Task<bool> DrainAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var dbContext = services.GetRequiredService<NotificationsDbContext>();
        var context = new InAppContext(
            services.GetRequiredService<IMembershipDirectory>(),
            services.GetRequiredService<IClock>(),
            dbContext);
        var publisher = services.GetRequiredService<IRealtimeMemberPublisher>();

        var now = context.Clock.UtcNow;
        var oldest = now - MaxEventAge;
        var eventNames = EventNames.ToArray();
        var candidates = await dbContext.Outbox
            .AsNoTracking()
            .Where(record => eventNames.Contains(record.EventName) && record.OccurredAt >= oldest)
            .Where(record => !dbContext.Inbox.Any(entry =>
                entry.Consumer == Consumer
                && entry.MessageId == record.Id
                && (entry.ProcessedAt != null || entry.ClaimedUntil >= now)))
            .OrderBy(record => record.OccurredAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var record in candidates)
        {
            try
            {
                await ProcessAsync(dbContext, context, publisher, record, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // The claim stays alive: it is retried when the lease expires, and cut after MaxAttempts. The rest of the batch goes on.
                LogMessageFailed(logger, record.Id, Consumer, exception);
            }
            finally
            {
                // A message that failed halfway leaves no tracked entities to the next one.
                dbContext.ChangeTracker.Clear();
            }
        }

        return candidates.Count == BatchSize;
    }

    private async Task ProcessAsync(
        NotificationsDbContext dbContext,
        InAppContext context,
        IRealtimeMemberPublisher publisher,
        OutboxRecord record,
        CancellationToken cancellationToken)
    {
        var now = context.Clock.UtcNow;
        var attempts = await InboxClaims.TryClaimAsync(dbContext, Consumer, record.Id, now, Lease, cancellationToken);
        if (attempts is null)
        {
            // Another replica has it, or it is already processed.
            return;
        }

        IReadOnlyList<InAppNotification> entries = [];
        IReadOnlyList<InAppResolution> resolutions = [];
        if (attempts > MaxAttempts)
        {
            // What fails may be the payload itself, so it is not read again.
            LogAttemptsExhausted(logger, record.Id, attempts.Value, Consumer);
        }
        else
        {
            entries = await BuildAsync(record, context, cancellationToken);
            resolutions = await ResolutionsAsync(record, context, cancellationToken);
        }

        var inserted = await InAppNotificationWriter.SaveAsync(
            dbContext, Consumer, record.Id, entries, resolutions, now, publisher, cancellationToken,
            onInserted: created => OnInsertedAsync(dbContext, record, created, now, cancellationToken));
        LogEntriesCreated(logger, record.Id, inserted, Consumer);
    }
}

/// <summary>Spec D-13: the entries of <paramref name="Kind"/> about <paramref name="ResourceId"/> that are still unresolved become resolved
/// (and read) with <paramref name="Resolution"/>. An entry that already had a resolution keeps it.
/// <paramref name="MemberId"/> narrows it to that member's entry (task-notices spec D-2: a reassignment must not close the new assignee's).</summary>
internal sealed record InAppResolution(Guid TenantId, InAppNotificationKind Kind, Guid ResourceId, string Resolution, Guid? MemberId = null);

/// <summary>What a consumer needs to build its entries, resolved in the scope of the tick. <paramref name="Database"/> is the context of
/// the claim and the write: only to read before building (the wholesale consumer looks for the entries it resolves).</summary>
internal sealed record InAppContext(IMembershipDirectory Memberships, IClock Clock, NotificationsDbContext Database);
