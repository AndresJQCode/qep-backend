using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>
/// Spec 2026-11-22 §12: writes <c>messaging.conversation.assigned.v1</c> next to the event row that caused it, in the same transaction,
/// by two doors like the row itself: EF for the handlers (<see cref="Add"/>) and SQL for the ingestion (<see cref="InsertAsync"/>). Both
/// stamp the outbox row with the time of the write (<c>now</c>), not the time of the fact: the bell only claims events younger than 24 h
/// (D-6), and a late redelivery from Meta carries an hour of the past that would otherwise be lost. The fact's hour travels in the payload.
/// </summary>
internal static class ConversationAssignmentOutbox
{
    public static void Add(
        MessagingDbContext dbContext, Guid tenantId, Guid conversationId, ConversationEvent value, DateTimeOffset eventAt, DateTimeOffset now)
    {
        if (ConversationAssignmentNotice.From(value) is not { } notice)
        {
            return;
        }

        dbContext.OutboxMessages.Add(new MessagingOutboxRecord
        {
            Id = Guid.CreateVersion7(),
            EventName = ConversationAssignmentNotice.EventName,
            PayloadJson = PayloadOf(tenantId, conversationId, notice, eventAt),
            CorrelationId = conversationId.ToString("D"),
            OccurredAt = now.ToUniversalTime(),
            Attempts = 0,
        });
    }

    public static async Task InsertAsync(
        MessagingDbContext dbContext, Guid tenantId, Guid conversationId, ConversationEvent value, DateTimeOffset eventAt, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (ConversationAssignmentNotice.From(value) is not { } notice)
        {
            return;
        }

        var payload = PayloadOf(tenantId, conversationId, notice, eventAt);
        var correlationId = conversationId.ToString("D");
        // The same columns the projection maps; processed_at and last_error stay null. A raw INSERT rings the outbox doorbell too.
        await dbContext.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO platform.outbox_messages (id, event_name, payload, correlation_id, occurred_at, attempts)
            VALUES ({Guid.CreateVersion7()}, {ConversationAssignmentNotice.EventName}, {payload}::jsonb, {correlationId}, {now.ToUniversalTime()}, 0)
            """,
            cancellationToken);
    }

    // camelCase, read by name with JsonDocument by the consumer (spec §12).
    private static string PayloadOf(Guid tenantId, Guid conversationId, AssignmentNotice notice, DateTimeOffset eventAt) =>
        JsonSerializer.Serialize(
            new
            {
                tenantId,
                conversationId,
                memberId = notice.MemberId,
                reason = notice.Reason,
                actorMemberId = notice.ActorMemberId,
                occurredAt = eventAt.ToUniversalTime(),
            },
            JsonSerializerOptions.Web);
}
