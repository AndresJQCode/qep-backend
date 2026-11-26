namespace Modules.ServiceContracts.Infrastructure.Persistence;

// Write projection of the platform outbox, owned by Tenancy (a copy of CatalogOutboxMessage). ServiceContracts writes here the integration
// events other modules consume (the bell of Notifications, spec 2026-11-22 §12), in the same transaction as the change that causes them.
// Mapped with ExcludeFromMigrations: it creates no table.
internal sealed class ServiceContractsOutboxMessage
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
