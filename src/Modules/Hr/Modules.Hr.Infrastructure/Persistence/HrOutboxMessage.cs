namespace Modules.Hr.Infrastructure.Persistence;

// Write projection of the platform outbox, owned by Tenancy (a copy of ServiceContractsOutboxMessage). Hr writes here the integration
// events other modules consume (the bell of Notifications, spec 2026-11-23 §9.2), in the same transaction as the change that causes
// them. Mapped with ExcludeFromMigrations: it creates no table.
internal sealed class HrOutboxMessage
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

/// <summary>The row of <c>hr.tenant_runs</c>: the claim of the daily run of a tenant (spec 2026-11-23 §9.1). Written with raw SQL.</summary>
internal sealed class TenantRun
{
    public Guid TenantId { get; init; }

    public DateOnly RunDate { get; init; }

    public DateTimeOffset ClaimedUntil { get; init; }

    public DateTimeOffset? FinishedAt { get; init; }
}

/// <summary>The row of <c>hr.daily_digests</c>: one per manager and day, its deduplication (spec 2026-11-23 §9.1). Written with raw SQL.</summary>
internal sealed class DailyDigest
{
    public Guid TenantId { get; init; }

    public Guid MemberId { get; init; }

    public DateOnly RunDate { get; init; }

    public string Status { get; init; } = string.Empty;

    public string? FailureCode { get; init; }

    public DateTimeOffset ClaimedUntil { get; init; }

    public DateTimeOffset? SentAt { get; init; }
}
