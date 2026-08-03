namespace Modules.Notifications.Infrastructure.Persistence;

// Per-consumer idempotency guard: a processed outbox message id for this module's
// consumer. (consumer, message_id) is unique.
internal sealed class NotificationInboxMessage
{
    public string Consumer { get; init; } = string.Empty;

    public Guid MessageId { get; init; }

    public DateTimeOffset ProcessedAt { get; init; }
}

// Read-only projection of the platform Outbox, consumed independently by this module.
internal sealed class OutboxRecord
{
    public Guid Id { get; init; }

    public string EventName { get; init; } = string.Empty;

    public string PayloadJson { get; init; } = "{}";

    public DateTimeOffset OccurredAt { get; init; }
}
