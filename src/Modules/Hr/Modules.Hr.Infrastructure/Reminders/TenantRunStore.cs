using Microsoft.EntityFrameworkCore;
using Modules.Hr.Infrastructure.Persistence;

namespace Modules.Hr.Infrastructure.Reminders;

/// <summary>
/// The row <c>hr.tenant_runs(tenant, day)</c> (spec 2026-11-23 §9.1): with <c>finished_at</c> a tick does nothing; without it, only one
/// replica holds it at a time and another retakes it when the lease expires. Same statements as the one of ServiceContracts.
/// </summary>
internal sealed class TenantRunStore(HrDbContext dbContext)
{
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    /// <summary>One statement: the insert, or the retake of an unfinished row whose lease expired. No row returned means another
    /// replica has it or the day already finished. Atomic in Postgres even if two replicas arrive at the same instant.</summary>
    public async Task<bool> TryClaimAsync(Guid tenantId, DateOnly runDate, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var leaseUntil = now + Lease;
        var claimed = await dbContext.Database
            .SqlQuery<Guid>(
                $"""
                INSERT INTO hr.tenant_runs (tenant_id, run_date, claimed_until, finished_at)
                VALUES ({tenantId}, {runDate}, {leaseUntil}, NULL)
                ON CONFLICT (tenant_id, run_date) DO UPDATE SET claimed_until = EXCLUDED.claimed_until
                WHERE hr.tenant_runs.finished_at IS NULL AND hr.tenant_runs.claimed_until < {now}
                RETURNING tenant_id AS "Value"
                """)
            .ToListAsync(cancellationToken);
        return claimed.Count > 0;
    }

    /// <summary>Closes the day only if no digest is left <c>Sending</c>: closing it with a live one (of a replica that retook or lost the
    /// run) would leave it hanging for ever. Open, the next tick claims the run when its lease expires. False: the day stays open.</summary>
    public async Task<bool> TryFinishAsync(
        Guid tenantId, DateOnly runDate, DateTimeOffset finishedAt, CancellationToken cancellationToken) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE hr.tenant_runs SET finished_at = {finishedAt}
            WHERE tenant_id = {tenantId} AND run_date = {runDate} AND finished_at IS NULL
              AND NOT EXISTS (
                  SELECT 1 FROM hr.daily_digests d
                  WHERE d.tenant_id = {tenantId} AND d.run_date = {runDate} AND d.status = 'Sending')
            """,
            cancellationToken) == 1;
}
