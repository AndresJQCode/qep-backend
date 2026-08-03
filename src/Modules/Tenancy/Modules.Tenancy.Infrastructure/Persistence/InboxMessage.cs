namespace Modules.Tenancy.Infrastructure.Persistence;

// Dedupe ledger for at-least-once integration-event delivery. Uniqueness by
// (Consumer, MessageId) guarantees a given consumer applies an event once even
// if it is redelivered.
internal sealed class InboxMessage
{
    public string Consumer { get; init; } = string.Empty;

    public Guid MessageId { get; init; }

    public DateTimeOffset ProcessedAt { get; init; }
}
