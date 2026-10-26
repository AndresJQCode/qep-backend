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

    public async Task<int?> TryClaimAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var leases = LeaseArray;
        var rows = await dbContext.Database.SqlQuery<ClaimRow>(
            $"""
            UPDATE shopify.city_correction_jobs
               SET lease_until = {now} + ({leases})[LEAST(attempts + 1, cardinality({leases}))],
                   attempts = attempts + 1
             WHERE id = {id} AND status = {Pending} AND (lease_until IS NULL OR lease_until < {now})
            RETURNING attempts AS "Attempts"
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

    private sealed record ClaimRow(int Attempts);
}
