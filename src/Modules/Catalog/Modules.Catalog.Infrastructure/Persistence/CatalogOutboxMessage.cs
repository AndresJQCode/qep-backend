namespace Modules.Catalog.Infrastructure.Persistence;

// Write projection of the platform Outbox, owned by Tenancy. Catalog inserts operational
// audit events here in the same transaction as the catalogue change; the Audit module
// projection worker consumes them. Mapped ExcludeFromMigrations.
internal sealed class CatalogOutboxMessage
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
