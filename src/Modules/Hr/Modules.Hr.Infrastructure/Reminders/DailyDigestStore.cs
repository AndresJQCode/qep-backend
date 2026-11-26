using Microsoft.EntityFrameworkCore;
using Modules.Hr.Infrastructure.Persistence;

namespace Modules.Hr.Infrastructure.Reminders;

/// <summary>The record of the digest to each manager (spec 2026-11-23 §9.1): one row per manager and day is its deduplication.</summary>
internal sealed class DailyDigestStore(HrDbContext dbContext)
{
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    private const int FailureCodeMaxLength = 64;

    /// <summary>The insert of the <c>Sending</c> row, or the retake of a <c>Sending</c> one whose lease expired, in one statement. No row
    /// returned: the digest of the day already went out, failed, or another replica has it.</summary>
    public async Task<bool> TryClaimAsync(
        Guid tenantId, Guid memberId, DateOnly runDate, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var leaseUntil = now + Lease;
        var claimed = await dbContext.Database
            .SqlQuery<Guid>(
                $"""
                INSERT INTO hr.daily_digests (tenant_id, member_id, run_date, status, claimed_until)
                VALUES ({tenantId}, {memberId}, {runDate}, 'Sending', {leaseUntil})
                ON CONFLICT (tenant_id, member_id, run_date) DO UPDATE SET claimed_until = EXCLUDED.claimed_until
                WHERE hr.daily_digests.status = 'Sending' AND hr.daily_digests.claimed_until < {now}
                RETURNING member_id AS "Value"
                """)
            .ToListAsync(cancellationToken);
        return claimed.Count > 0;
    }

    /// <summary>A <c>Sending</c> row whose lease expired and that nobody retook becomes <c>Failed send_failed</c> instead of hanging.</summary>
    public Task FailStaleAsync(Guid tenantId, DateOnly today, DateTimeOffset now, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE hr.daily_digests SET status = 'Failed', failure_code = 'send_failed'
            WHERE tenant_id = {tenantId} AND run_date <= {today} AND status = 'Sending' AND claimed_until < {now}
            """,
            cancellationToken);

    public Task MarkSentAsync(Guid tenantId, Guid memberId, DateOnly runDate, DateTimeOffset now, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE hr.daily_digests SET status = 'Sent', sent_at = {now}, failure_code = NULL
            WHERE tenant_id = {tenantId} AND member_id = {memberId} AND run_date = {runDate} AND status = 'Sending'
            """,
            cancellationToken);

    public Task MarkFailedAsync(
        Guid tenantId, Guid memberId, DateOnly runDate, string failureCode, CancellationToken cancellationToken)
    {
        var code = failureCode.Length > FailureCodeMaxLength ? failureCode[..FailureCodeMaxLength] : failureCode;
        return dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE hr.daily_digests SET status = 'Failed', failure_code = {code}
            WHERE tenant_id = {tenantId} AND member_id = {memberId} AND run_date = {runDate} AND status = 'Sending'
            """,
            cancellationToken);
    }
}
