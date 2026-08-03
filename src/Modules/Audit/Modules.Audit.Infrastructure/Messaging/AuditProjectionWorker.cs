using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using BuildingBlocks.Application;
using Modules.Audit.Domain;
using Modules.Audit.Infrastructure.Persistence;

namespace Modules.Audit.Infrastructure.Messaging;

// Operational audit path (ADR 0019): projects audit events published to the platform
// Outbox into the append-only audit.entries store. Idempotent via this module's own
// inbox keyed by (consumer, outbox message id); each message is committed independently
// so one failure does not block the batch. Critical/security audits do not use this path
// — they are written atomically inside the producer transaction.
internal sealed partial class AuditProjectionWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<AuditProjectionWorker> logger) : BackgroundService
{
    private const string Consumer = "audit.projection";
    internal const string EventName = "platform.audit.recorded.v1";
    private const int BatchSize = 50;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    [LoggerMessage(Level = LogLevel.Error, Message = "Audit projection tick failed.")]
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
        var dbContext = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        var pending = await dbContext.Outbox
            .Where(record => record.EventName == EventName)
            .Where(record => !dbContext.Inbox.Any(entry =>
                entry.Consumer == Consumer && entry.MessageId == record.Id))
            .OrderBy(record => record.OccurredAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var record in pending)
        {
            await ProjectAsync(dbContext, clock, record, cancellationToken);
        }
    }

    private static async Task ProjectAsync(
        AuditDbContext dbContext,
        IClock clock,
        OutboxRecord record,
        CancellationToken cancellationToken)
    {
        var entry = ParsePayload(record.PayloadJson);
        dbContext.Entries.Add(entry);
        dbContext.Inbox.Add(new AuditInboxMessage
        {
            Consumer = Consumer,
            MessageId = record.Id,
            ProcessedAt = clock.UtcNow,
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static AuditEntry ParsePayload(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;

        Guid? tenantId = root.TryGetProperty("tenantId", out var tenant)
            && tenant.ValueKind is not JsonValueKind.Null
            ? tenant.GetGuid()
            : null;
        var actorType = root.TryGetProperty("actorType", out var actor)
            && Enum.TryParse<AuditActorType>(actor.GetString(), out var parsed)
            ? parsed
            : AuditActorType.System;
        var changedFields = root.TryGetProperty("changedFields", out var changes)
            && changes.ValueKind is JsonValueKind.Array
            ? changes.GetRawText()
            : "[]";

        return AuditEntry.Create(
            tenantId,
            root.GetProperty("actorId").GetGuid(),
            actorType,
            root.GetProperty("action").GetString() ?? string.Empty,
            root.GetProperty("resourceType").GetString() ?? string.Empty,
            root.GetProperty("resourceId").GetString() ?? string.Empty,
            root.TryGetProperty("outcome", out var outcome) ? outcome.GetString() ?? string.Empty : string.Empty,
            changedFields,
            root.TryGetProperty("source", out var source) ? source.GetString() ?? string.Empty : string.Empty,
            root.GetProperty("occurredAt").GetDateTimeOffset());
    }
}
