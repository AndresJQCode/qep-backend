using Microsoft.EntityFrameworkCore;
using Modules.Billing.Infrastructure.Persistence;

namespace Modules.Billing.Infrastructure.Reminders;

/// <summary>
/// La fila <c>billing.tenant_runs(tenant, día)</c> (spec §2, §3.3 paso 2): con <c>finished_at</c> un tick no hace nada; sin él, sólo
/// una réplica la tiene reclamada a la vez y otra la retoma cuando vence el lease.
/// </summary>
internal sealed class TenantRunStore(BillingDbContext dbContext)
{
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Un solo statement: el insert, o la retoma de una fila sin terminar con el lease vencido. Sin fila devuelta, otra réplica la
    /// tiene o el día ya terminó. Atómico en Postgres aunque dos réplicas lleguen en el mismo instante.
    /// </summary>
    public async Task<bool> TryClaimAsync(Guid tenantId, DateOnly runDate, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var leaseUntil = now + Lease;
        var claimed = await dbContext.Database
            .SqlQuery<Guid>(
                $"""
                INSERT INTO billing.tenant_runs (tenant_id, run_date, claimed_until, finished_at)
                VALUES ({tenantId}, {runDate}, {leaseUntil}, NULL)
                ON CONFLICT (tenant_id, run_date) DO UPDATE SET claimed_until = EXCLUDED.claimed_until
                WHERE billing.tenant_runs.finished_at IS NULL AND billing.tenant_runs.claimed_until < {now}
                RETURNING tenant_id AS "Value"
                """)
            .ToListAsync(cancellationToken);
        return claimed.Count > 0;
    }

    public Task MarkFinishedAsync(Guid tenantId, DateOnly runDate, DateTimeOffset now, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE billing.tenant_runs SET finished_at = {now}
            WHERE tenant_id = {tenantId} AND run_date = {runDate}
            """,
            cancellationToken);
}
