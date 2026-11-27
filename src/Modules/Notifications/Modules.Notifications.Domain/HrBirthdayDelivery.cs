namespace Modules.Notifications.Domain;

/// <summary>HR birthday notices: where a delivery ends. Stored by name in a <c>varchar(16)</c>.</summary>
public enum HrBirthdayDeliveryStatus
{
    Pending,
    Sent,
    Failed,
    Skipped,
}

/// <summary>The two channels of a birthday digest, stored by name in <c>channel</c>.</summary>
public static class HrBirthdayDeliveryChannels
{
    public const string WhatsApp = "whatsapp";
    public const string Email = "email";
}

/// <summary>Why the worker did not send a delivery (<c>Skipped</c>), or why it gave up on it (<c>Failed</c>); stored in <c>result_code</c>.</summary>
public static class HrBirthdayDeliverySkipReasons
{
    /// <summary>The run date is over in the tenant's time zone: a «Hoy» sent the next day would be wrong.</summary>
    public const string Stale = "stale";
    public const string NoPhone = "no_phone";
    public const string NoEmail = "no_email";
    public const string MemberInactive = "member_inactive";
    public const string AttemptsExhausted = "attempts_exhausted";
}

/// <summary>
/// HR birthday notices, spec 2026-11-26 §5.3: one channel of one birthday digest for one manager and day. Rows are created by raw SQL
/// (<c>ON CONFLICT DO NOTHING</c> on <c>(tenant_id, member_id, run_date, channel)</c>) in the same transaction as the bell entry, so
/// <see cref="Create"/> is for tests. <see cref="PayloadJson"/> holds the names only (today, upcoming, totals, upcoming date): never a birth
/// date or an age. <see cref="SentByMemberId"/> is the member the WhatsApp message is authored by (Messaging requires an author);
/// <see cref="ConnectionId"/> is set for WhatsApp only. Every transition is only valid from <see cref="HrBirthdayDeliveryStatus.Pending"/>.
/// </summary>
public sealed class HrBirthdayDelivery
{
    public const int ResultCodeMaxLength = 128;

    private HrBirthdayDelivery()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid MemberId { get; private set; }

    public DateOnly RunDate { get; private set; }

    public string Channel { get; private set; } = string.Empty;

    public Guid? ConnectionId { get; private set; }

    public Guid SentByMemberId { get; private set; }

    public string PayloadJson { get; private set; } = "{}";

    public HrBirthdayDeliveryStatus Status { get; private set; }

    public string? ResultCode { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset? ClaimedUntil { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public static HrBirthdayDelivery Create(
        Guid tenantId,
        Guid memberId,
        DateOnly runDate,
        string channel,
        Guid? connectionId,
        Guid sentByMemberId,
        string payloadJson,
        DateTimeOffset expiresAt,
        DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(payloadJson);
        return new HrBirthdayDelivery
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            MemberId = memberId,
            RunDate = runDate,
            Channel = channel,
            ConnectionId = connectionId,
            SentByMemberId = sentByMemberId,
            PayloadJson = payloadJson,
            Status = HrBirthdayDeliveryStatus.Pending,
            ExpiresAt = expiresAt,
            CreatedAt = createdAt,
        };
    }

    /// <summary>A replica takes the row for a lease; the attempt is counted at claim time, so a crash cannot loop forever.</summary>
    public void Claim(DateTimeOffset until)
    {
        EnsurePending();
        Attempts++;
        ClaimedUntil = until;
    }

    public void MarkSent(DateTimeOffset now) => Complete(HrBirthdayDeliveryStatus.Sent, null, now);

    public void Skip(string reason, DateTimeOffset now) => Complete(HrBirthdayDeliveryStatus.Skipped, reason, now);

    public void Fail(string code, DateTimeOffset now) => Complete(HrBirthdayDeliveryStatus.Failed, code, now);

    private void Complete(HrBirthdayDeliveryStatus status, string? code, DateTimeOffset now)
    {
        EnsurePending();
        if (status != HrBirthdayDeliveryStatus.Sent)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(code);
        }

        Status = status;
        ResultCode = code is { Length: > ResultCodeMaxLength } ? code[..ResultCodeMaxLength] : code;
        ClaimedUntil = null;
        CompletedAt = now;
    }

    private void EnsurePending()
    {
        if (Status != HrBirthdayDeliveryStatus.Pending)
        {
            throw new InvalidOperationException($"The delivery is {Status}; only a pending one changes.");
        }
    }
}
