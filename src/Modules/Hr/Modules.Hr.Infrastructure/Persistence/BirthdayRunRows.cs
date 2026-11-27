namespace Modules.Hr.Infrastructure.Persistence;

/// <summary>The row of <c>hr.birthday_notice_runs</c>: the claim of the daily birthday run of a tenant. Written with raw SQL.</summary>
internal sealed class BirthdayNoticeRun
{
    public Guid TenantId { get; init; }

    public DateOnly RunDate { get; init; }

    public DateTimeOffset ClaimedUntil { get; init; }

    public DateTimeOffset? FinishedAt { get; init; }
}

/// <summary>The row of <c>hr.birthday_notice_digests</c>: one per manager and day, its deduplication. Written with raw SQL.</summary>
internal sealed class BirthdayNoticeDigest
{
    public Guid TenantId { get; init; }

    public Guid MemberId { get; init; }

    public DateOnly RunDate { get; init; }

    public string Status { get; init; } = string.Empty;

    public string? FailureCode { get; init; }

    public DateTimeOffset ClaimedUntil { get; init; }

    public DateTimeOffset? SentAt { get; init; }
}
