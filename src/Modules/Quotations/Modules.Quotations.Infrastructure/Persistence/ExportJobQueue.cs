using Microsoft.EntityFrameworkCore;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Infrastructure.Persistence;

internal sealed class ExportJobQueue(QuotationsDbContext dbContext) : IExportJobQueue
{
    public void Add(ExportJob job) => dbContext.ExportJobs.Add(job);

    public Task<int> CountPendingAsync(
        Guid tenantId, Guid requestedBy, CancellationToken cancellationToken) =>
        dbContext.ExportJobs.CountAsync(
            job => job.TenantId == tenantId
                && job.RequestedBy == requestedBy
                && (job.Status == ExportJobStatus.Pending || job.Status == ExportJobStatus.Processing),
            cancellationToken);

    /// <summary>
    /// D6: un solo UPDATE que elige y toma. El SELECT interno bloquea la fila elegida y salta
    /// las que ya bloqueó otra transacción (SKIP LOCKED), así que dos workers nunca se llevan el
    /// mismo job ni se esperan entre sí. Toma un Pending vencido o un Processing con el lease
    /// vencido (worker muerto), suma un intento y fija el lease: la misma transición que
    /// <see cref="ExportJob.Claim"/>.
    ///
    /// Sin transacción propia: fuera de una, el UPDATE commitea solo y el lease se ve desde otros
    /// procesos ya. El resultado queda trackeado —FromSql sin componer— y cerrar el job es un
    /// guardado normal de la unidad de trabajo, con <c>attempts</c> como token de concurrencia.
    /// </summary>
    public async Task<ExportJob?> ClaimNextAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var leaseUntil = now.Add(ExportJob.LeaseDuration);
        var claimed = await dbContext.ExportJobs
            .FromSql($"""
                UPDATE quotations.export_jobs AS job
                SET status = 'Processing',
                    attempts = job.attempts + 1,
                    locked_until = {leaseUntil}
                WHERE job.id = (
                    SELECT candidate.id
                    FROM quotations.export_jobs AS candidate
                    WHERE (candidate.status = 'Pending' AND candidate.next_attempt_at <= {now})
                       OR (candidate.status = 'Processing' AND candidate.locked_until < {now})
                    ORDER BY candidate.next_attempt_at, candidate.id
                    LIMIT 1
                    FOR UPDATE SKIP LOCKED)
                RETURNING job.*
                """)
            // Nada de FirstOrDefaultAsync: componer sobre FromSql envolvería el UPDATE en un
            // SELECT, que Postgres rechaza.
            .ToListAsync(cancellationToken);

        return claimed.SingleOrDefault();
    }

    public Task<int> PurgeFinishedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken) =>
        dbContext.ExportJobs
            .Where(job => (job.Status == ExportJobStatus.Completed || job.Status == ExportJobStatus.Failed)
                && job.CompletedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
}
