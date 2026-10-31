using Microsoft.EntityFrameworkCore;
using Modules.ServiceContracts.Infrastructure.Persistence;

namespace Modules.ServiceContracts.Infrastructure.Reminders;

/// <summary>
/// La fila <c>billing.tenant_runs(tenant, día)</c> (spec §2, §3.3 paso 2): con <c>finished_at</c> un tick no hace nada; sin él, sólo
/// una réplica la tiene reclamada a la vez y otra la retoma cuando vence el lease.
/// </summary>
internal sealed class TenantRunStore(ServiceContractsDbContext dbContext)
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

    /// <summary>
    /// Cierra el día sólo si no le queda ningún mensaje ni digest <c>Sending</c>. Con <c>finished_at</c> ningún tick vuelve a mirar
    /// el día, así que cerrarlo con un <c>Sending</c> vivo —de otra réplica que retomó o perdió la corrida, o de un envío cuyo
    /// <c>MarkSent</c> falló— lo dejaría colgado para siempre. Abierto, el tick siguiente reclama la corrida cuando vence su lease y
    /// retoma el mensaje cuando vence el suyo (spec §2). False: el día sigue abierto.
    /// </summary>
    public async Task<bool> TryFinishAsync(
        Guid tenantId, DateOnly runDate, DateTimeOffset finishedAt, CancellationToken cancellationToken) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE billing.tenant_runs SET finished_at = {finishedAt}
            WHERE tenant_id = {tenantId} AND run_date = {runDate} AND finished_at IS NULL
              AND NOT EXISTS (
                  SELECT 1 FROM billing.customer_messages m
                  WHERE m.tenant_id = {tenantId} AND m.run_date = {runDate} AND m.status = 'Sending')
              AND NOT EXISTS (
                  SELECT 1 FROM billing.daily_digests d
                  WHERE d.tenant_id = {tenantId} AND d.run_date = {runDate} AND d.status = 'Sending')
            """,
            cancellationToken) == 1;
}
