using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Wms.Application;
using Modules.Wms.Domain;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.Infrastructure.Persistence;

internal sealed class VariantProfileRepository(
    WmsDbContext dbContext,
    IWmsCatalogReader catalogReader,
    IIdentifierRepository identifiers,
    IWmsAuditRecorder auditRecorder,
    IClock clock) : IVariantProfileRepository
{
    // Sin persona detrás: el perfil nace porque alguien abrió la pantalla, no porque decidió crearlo. Mismo actor vacío que el resto
    // de los módulos usan para lo que hace el sistema.
    private static readonly Guid SystemActorId = Guid.Empty;

    public void Add(VariantLogisticsProfile profile) => dbContext.VariantProfiles.Add(profile);

    public Task<VariantLogisticsProfile?> FindAsync(Guid tenantId, Guid variantId, CancellationToken ct) =>
        dbContext.VariantProfiles
            .Include(profile => profile.Conversions)
            .FirstOrDefaultAsync(profile => profile.TenantId == tenantId && profile.VariantId == variantId, ct);

    public async Task<IReadOnlyList<VariantLogisticsProfile>> ListAsync(
        Guid tenantId, IReadOnlyCollection<Guid> variantIds, CancellationToken ct)
    {
        if (variantIds.Count == 0)
        {
            return [];
        }

        var ids = variantIds.ToArray();
        return await dbContext.VariantProfiles
            .AsNoTracking()
            .Include(profile => profile.Conversions)
            .Where(profile => profile.TenantId == tenantId && ids.Contains(profile.VariantId))
            .ToListAsync(ct);
    }

    public async Task<VariantLogisticsProfile> GetOrCreateDefaultAsync(Guid tenantId, Guid variantId, CancellationToken ct)
    {
        if (await FindAsync(tenantId, variantId, ct) is { } existing)
        {
            return existing;
        }

        // P-8: Catalog es la autoridad de lo que la variante es. Un id que no conoce (o de otro tenant) no se perfila.
        var snapshot = (await catalogReader.GetVariantsAsync(tenantId, [variantId], ct))
            .FirstOrDefault(variant => variant.VariantId == variantId)
            ?? throw new WmsDomainException(WmsErrorCodes.ProfileVariantUnknown, "The variant does not exist in the catalog.");

        var now = clock.UtcNow;
        var profile = VariantLogisticsProfile.CreateDefault(tenantId, variantId, snapshot.ProductId, snapshot.PackagingUnits, now);
        dbContext.VariantProfiles.Add(profile);
        identifiers.AddRange(Identifier.ForVariant(tenantId, variantId, snapshot.Code, snapshot.Barcode, now));
        auditRecorder.Record(
            tenantId,
            SystemActorId,
            WmsActorType.System,
            WmsAuditActions.ProfileCreated,
            WmsAuditActions.Resources.Profile,
            variantId.ToString(),
            new Dictionary<string, string?> { ["code"] = snapshot.Code },
            now);
        return profile;
    }
}
