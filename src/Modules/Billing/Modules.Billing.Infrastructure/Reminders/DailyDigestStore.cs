using Microsoft.EntityFrameworkCore;
using Modules.Billing.Infrastructure.Persistence;

namespace Modules.Billing.Infrastructure.Reminders;

/// <summary>El registro del email diario al equipo (spec §2): una fila por encargado y día es su deduplicación.</summary>
internal sealed class DailyDigestStore(BillingDbContext dbContext)
{
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    private const int FailureCodeMaxLength = 64;

    /// <summary>
    /// El insert de la fila <c>Sending</c>, o la retoma de una <c>Sending</c> con el lease vencido, en un statement. Sin fila
    /// devuelta, el email del día ya salió, falló o lo tiene otra réplica.
    /// </summary>
    public async Task<bool> TryClaimAsync(
        Guid tenantId, Guid memberId, DateOnly runDate, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var leaseUntil = now + Lease;
        var claimed = await dbContext.Database
            .SqlQuery<Guid>(
                $"""
                INSERT INTO billing.daily_digests (tenant_id, member_id, run_date, status, claimed_until)
                VALUES ({tenantId}, {memberId}, {runDate}, 'Sending', {leaseUntil})
                ON CONFLICT (tenant_id, member_id, run_date) DO UPDATE SET claimed_until = EXCLUDED.claimed_until
                WHERE billing.daily_digests.status = 'Sending' AND billing.daily_digests.claimed_until < {now}
                RETURNING member_id AS "Value"
                """)
            .ToListAsync(cancellationToken);
        return claimed.Count > 0;
    }

    /// <summary>
    /// El mismo cierre que <see cref="CustomerMessageStore.FailStaleAsync"/>: un digest <c>Sending</c> vencido que el paso 5 no volvió a
    /// reclamar (el encargado ya no tiene filas, o el día no terminó) pasa a <c>Failed send_failed</c>.
    /// </summary>
    public Task FailStaleAsync(Guid tenantId, DateOnly today, DateTimeOffset now, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE billing.daily_digests SET status = 'Failed', failure_code = 'send_failed'
            WHERE tenant_id = {tenantId} AND run_date <= {today} AND status = 'Sending' AND claimed_until < {now}
            """,
            cancellationToken);

    public Task MarkSentAsync(Guid tenantId, Guid memberId, DateOnly runDate, DateTimeOffset now, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE billing.daily_digests SET status = 'Sent', sent_at = {now}, failure_code = NULL
            WHERE tenant_id = {tenantId} AND member_id = {memberId} AND run_date = {runDate} AND status = 'Sending'
            """,
            cancellationToken);

    public Task MarkFailedAsync(
        Guid tenantId, Guid memberId, DateOnly runDate, string failureCode, CancellationToken cancellationToken)
    {
        var code = failureCode.Length > FailureCodeMaxLength ? failureCode[..FailureCodeMaxLength] : failureCode;
        return dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE billing.daily_digests SET status = 'Failed', failure_code = {code}
            WHERE tenant_id = {tenantId} AND member_id = {memberId} AND run_date = {runDate} AND status = 'Sending'
            """,
            cancellationToken);
    }
}
