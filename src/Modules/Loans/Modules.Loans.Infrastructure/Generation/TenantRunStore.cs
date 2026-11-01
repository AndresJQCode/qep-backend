using Microsoft.EntityFrameworkCore;
using Modules.Loans.Infrastructure.Persistence;

namespace Modules.Loans.Infrastructure.Generation;

/// <summary>
/// La fila <c>loans.tenant_runs(tenant, día)</c>: con <c>finished_at</c> un tick no hace nada; sin él, sólo una réplica la
/// tiene reclamada a la vez y otra la retoma cuando vence el lease.
/// </summary>
internal sealed class TenantRunStore(LoansDbContext dbContext)
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
                INSERT INTO loans.tenant_runs (tenant_id, run_date, claimed_until, finished_at)
                VALUES ({tenantId}, {runDate}, {leaseUntil}, NULL)
                ON CONFLICT (tenant_id, run_date) DO UPDATE SET claimed_until = EXCLUDED.claimed_until
                WHERE loans.tenant_runs.finished_at IS NULL AND loans.tenant_runs.claimed_until < {now}
                RETURNING tenant_id AS "Value"
                """)
            .ToListAsync(cancellationToken);
        return claimed.Count > 0;
    }

    /// <summary>Cierra el día: con <c>finished_at</c> ningún tick vuelve a mirarlo. False si ya estaba cerrado.</summary>
    public async Task<bool> TryFinishAsync(
        Guid tenantId, DateOnly runDate, DateTimeOffset finishedAt, CancellationToken cancellationToken) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE loans.tenant_runs SET finished_at = {finishedAt}
            WHERE tenant_id = {tenantId} AND run_date = {runDate} AND finished_at IS NULL
            """,
            cancellationToken) == 1;
}
