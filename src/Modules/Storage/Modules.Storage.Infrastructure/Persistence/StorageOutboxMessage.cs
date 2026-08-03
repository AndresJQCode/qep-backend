namespace Modules.Storage.Infrastructure.Persistence;

// Write projection of the platform Outbox (owned by Tenancy). Storage inserts operational
// audit events here in the same transaction as the file operation; the Audit module's
// projection worker consumes them. Mapped ExcludeFromMigrations.
internal sealed class StorageOutboxMessage
{
    public Guid Id { get; init; }

    public string EventName { get; init; } = string.Empty;

    public string PayloadJson { get; init; } = "{}";

    public string CorrelationId { get; init; } = string.Empty;

    public DateTimeOffset OccurredAt { get; init; }

    public DateTimeOffset? ProcessedAt { get; init; }

    public int Attempts { get; init; }

    public string? LastError { get; init; }
}
