using Microsoft.EntityFrameworkCore;
using Modules.Shopify.Application;
using Modules.Shopify.Domain;

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
