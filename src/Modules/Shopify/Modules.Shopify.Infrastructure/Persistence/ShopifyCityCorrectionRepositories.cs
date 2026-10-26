using Microsoft.EntityFrameworkCore;
using Modules.Shopify.Application;
using Modules.Shopify.Domain;
using Modules.Shopify.Infrastructure.Workers;

namespace Modules.Shopify.Infrastructure.Persistence;

/// <summary>Con tracking: el llamador modifica y guarda con <c>IShopifyUnitOfWork</c>. <see cref="FindAsync"/> va por
/// <c>IX_city_corrections_synonym_target</c> y filtra además por <c>tenant_id</c>.</summary>
internal sealed class ShopifyCityCorrectionRepository(ShopifyDbContext dbContext) : IShopifyCityCorrectionRepository
{
    // Primero lo agregado en esta unidad de trabajo y todavía sin guardar: el índice único no lo ve, y agregar dos veces la
    // misma huella antes del guardado chocaría contra sí misma.
    public async Task<ShopifyCityCorrection?> FindAsync(
        Guid tenantId, Guid synonymId, ShopifyCityCorrectionTarget target, Guid targetId, CancellationToken cancellationToken) =>
        dbContext.CityCorrections.Local.FirstOrDefault(correction => Matches(correction, tenantId, synonymId, target, targetId))
        ?? await dbContext.CityCorrections.SingleOrDefaultAsync(
            correction => correction.TenantId == tenantId
                && correction.SynonymId == synonymId
                && correction.Target == target
                && correction.TargetId == targetId,
            cancellationToken);

    public Task<ShopifyCityCorrection?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.CityCorrections.SingleOrDefaultAsync(correction => correction.Id == id, cancellationToken);

    public void Add(ShopifyCityCorrection correction) => dbContext.CityCorrections.Add(correction);

    // Sin tenant a propósito: el sinónimo es de plataforma y corregirlo recorrige en todos los tenants (§4.6).
    public async Task<IReadOnlyList<ShopifyCityCorrection>> ListBySynonymAsync(Guid synonymId, CancellationToken cancellationToken) =>
        await dbContext.CityCorrections
            .AsNoTracking()
            .Where(correction => correction.SynonymId == synonymId)
            .OrderBy(correction => correction.Id)
            .ToListAsync(cancellationToken);

    private static bool Matches(
        ShopifyCityCorrection correction, Guid tenantId, Guid synonymId, ShopifyCityCorrectionTarget target, Guid targetId) =>
        correction.TenantId == tenantId && correction.SynonymId == synonymId && correction.Target == target
        && correction.TargetId == targetId;
}

/// <summary>Con tracking: el worker que corrige marca el trabajo y guarda con <c>IShopifyUnitOfWork</c>.</summary>
internal sealed class ShopifyCityCorrectionJobRepository(ShopifyDbContext dbContext) : IShopifyCityCorrectionJobRepository
{
    public Task<ShopifyCityCorrectionJob?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.CityCorrectionJobs.SingleOrDefaultAsync(job => job.Id == id, cancellationToken);

    public void Add(ShopifyCityCorrectionJob job) => dbContext.CityCorrectionJobs.Add(job);

    public async Task<bool> LockIfPendingAsync(Guid id, CancellationToken cancellationToken)
    {
        // Fuera de una transacción el FOR UPDATE se soltaría al terminar la sentencia y no frenaría a la otra réplica.
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("LockIfPendingAsync requires an open transaction (BeginTransactionAsync).");
        }

        var rows = await dbContext.Database.SqlQuery<StatusRow>(
            $"""SELECT status AS "Status" FROM shopify.city_correction_jobs WHERE id = {id} FOR UPDATE""").ToListAsync(cancellationToken);
        return rows is [{ Status: nameof(ShopifyCityCorrectionJobStatus.Pending) }];
    }

    private sealed record StatusRow(string Status);
}

/// <summary>
/// Spec 2026-10-26 D-C6 y S6: reintentar desde la consola los trabajos <c>Failed</c> de un sinónimo, en una sola sentencia que se
/// confirma en el acto. El estado va por nombre, como lo guarda la conversión de EF.
/// </summary>
internal sealed class ShopifyCityCorrectionJobs(ShopifyDbContext dbContext) : IShopifyCityCorrectionJobs
{
    private static readonly string Pending = nameof(ShopifyCityCorrectionJobStatus.Pending);

    private static readonly string Failed = nameof(ShopifyCityCorrectionJobStatus.Failed);

    public Task<int> RetryFailedAsync(Guid synonymId, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE shopify.city_correction_jobs
               SET status = {Pending}, attempts = 0, lease_until = NULL, last_error = NULL, skip_reason = NULL, processed_at = NULL
             WHERE synonym_id = {synonymId} AND status = {Failed}
            """,
            cancellationToken);
}

/// <summary>
/// Spec 2026-10-26 D-C6: la cola del worker que corrige sobre <c>shopify.city_correction_jobs</c>, por SQL. El reclamo es la regla de
/// <c>WebhookDeliveries</c>: una sentencia que se commitea sola y da un solo ganador entre réplicas, con el lease del intento que
/// consume (<see cref="ShopifyCityCorrectionWorker.Leases"/>). Los estados van por nombre, como los guarda la conversión de EF.
/// </summary>
internal sealed class ShopifyCityCorrectionJobQueue(ShopifyDbContext dbContext) : IShopifyCityCorrectionJobQueue
{
    // Un solo arreglo para el parámetro del reclamo (CA1861).
    private static readonly TimeSpan[] LeaseArray = [.. ShopifyCityCorrectionWorker.Leases];

    private static readonly string Pending = nameof(ShopifyCityCorrectionJobStatus.Pending);

    private static readonly string Failed = nameof(ShopifyCityCorrectionJobStatus.Failed);

    public async Task<IReadOnlyList<Guid>> ListPendingAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken) =>
        await dbContext.CityCorrectionJobs.AsNoTracking()
            .Where(job => job.Status == ShopifyCityCorrectionJobStatus.Pending && (job.LeaseUntil == null || job.LeaseUntil < now))
            .OrderBy(job => job.Id)
            .Select(job => job.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);

    // N2: SKIP LOCKED. Una réplica que está cerrando el trabajo tiene su fila tomada (LockIfPendingAsync); esperarla podría agotar
    // el tiempo del comando fuera del try de cada trabajo y cortar la pasada entera. Saltarla es lo correcto: está terminando.
    public async Task<int?> TryClaimAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var leases = LeaseArray;
        var rows = await dbContext.Database.SqlQuery<ClaimRow>(
            $"""
            WITH claimable AS (
                SELECT id FROM shopify.city_correction_jobs
                 WHERE id = {id} AND status = {Pending} AND (lease_until IS NULL OR lease_until < {now})
                   FOR UPDATE SKIP LOCKED)
            UPDATE shopify.city_correction_jobs job
               SET lease_until = {now} + ({leases})[LEAST(job.attempts + 1, cardinality({leases}))],
                   attempts = job.attempts + 1
              FROM claimable
             WHERE job.id = claimable.id
            RETURNING job.attempts AS "Attempts"
            """).ToListAsync(cancellationToken);
        return rows.Count == 1 ? rows[0].Attempts : null;
    }

    public Task MarkFailedAsync(Guid id, DateTimeOffset now, string lastError, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE shopify.city_correction_jobs
               SET status = {Failed}, last_error = {lastError}, processed_at = {now}
             WHERE id = {id} AND status = {Pending}
            """,
            cancellationToken);

    public async Task<IReadOnlyList<Guid>> RecordRequestedCityAsync(Guid id, Guid cityId, CancellationToken cancellationToken)
    {
        // Tiene que quedar aunque el intento caiga: una transacción abierta la arrastraría a su rollback.
        if (dbContext.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException("RecordRequestedCityAsync commits on its own and cannot run inside a transaction.");
        }

        var rows = await dbContext.Database.SqlQuery<RequestedRow>(
            $"""
            UPDATE shopify.city_correction_jobs
               SET requested_city_ids = CASE WHEN {cityId} = ANY(requested_city_ids) THEN requested_city_ids
                                             ELSE array_append(requested_city_ids, {cityId}) END
             WHERE id = {id}
            RETURNING requested_city_ids AS "Cities"
            """).ToListAsync(cancellationToken);
        return rows.Count == 1 ? rows[0].Cities : [cityId];
    }

    private sealed record ClaimRow(int Attempts);

    private sealed record RequestedRow(Guid[] Cities);
}
