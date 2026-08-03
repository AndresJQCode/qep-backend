using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modules.Audit.Domain;
using Modules.Identity.Infrastructure.Persistence;

namespace Modules.Identity.Infrastructure.Messaging;

// Consumes the membership-suspended/removed integration events from the platform
// Outbox and revokes every active session of the affected user. Idempotent via this
// module's own inbox keyed by (consumer, outbox message id): a redelivered message is
// skipped. Session.Revoke is itself idempotent, so double-processing across a crash
// is also safe, not just single-delivery.
//
// Deliberately revokes ALL of the user's sessions, not only the affected tenant's:
// the session token carries no tenant context (tenant/permissions are resolved live
// per request from membership state, see ExternalClaimsTransformation), so there is
// no per-tenant session to scope this to. Logging the user out of every tenant on a
// single-tenant suspend/removal is broader than strictly necessary but simpler and
// safe — see the session-cookie ADR for the trade-off.
internal sealed partial class SessionRevocationWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<SessionRevocationWorker> logger) : BackgroundService
{
    private const string Consumer = "identity.session-revocation";
    private const string SuspendedEvent = "tenancy.membership-suspended.v1";
    private const string RemovedEvent = "tenancy.membership-removed.v1";
    private const int BatchSize = 20;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    [LoggerMessage(Level = LogLevel.Error, Message = "Session revocation tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                LogTickFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task ProcessBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();

        var pending = await dbContext.Outbox
            .Where(record => record.EventName == SuspendedEvent || record.EventName == RemovedEvent)
            .Where(record => !dbContext.Inbox.Any(entry =>
                entry.Consumer == Consumer && entry.MessageId == record.Id))
            .OrderBy(record => record.OccurredAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var record in pending)
        {
            await RevokeAsync(dbContext, record, cancellationToken);
        }
    }

    private static async Task RevokeAsync(
        IdentityDbContext dbContext,
        OutboxRecord record,
        CancellationToken cancellationToken)
    {
        var userId = ParsePayload(record.PayloadJson);
        var reason = record.EventName == SuspendedEvent
            ? "membership_suspended"
            : "membership_removed";
        var now = DateTimeOffset.UtcNow;

        var activeSessions = await dbContext.Sessions
            .Where(session => session.UserId == new Modules.Identity.Domain.UserId(userId)
                && session.RevokedAt == null)
            .ToListAsync(cancellationToken);

        foreach (var session in activeSessions)
        {
            session.Revoke(now, reason);
            dbContext.AuditEntries.Add(AuditEntry.Create(
                tenantId: null,
                userId,
                AuditActorType.System,
                "identity.session.revoked",
                "session",
                session.Id.ToString(),
                "success",
                "[]",
                "identity",
                now));
        }

        dbContext.Inbox.Add(new IdentityInboxMessage
        {
            Consumer = Consumer,
            MessageId = record.Id,
            ProcessedAt = now,
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static Guid ParsePayload(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        return document.RootElement.GetProperty("userId").GetGuid();
    }
}
