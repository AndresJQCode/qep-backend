using Microsoft.EntityFrameworkCore;
using Modules.Wms.Application;
using Modules.Wms.Domain.Inventory;

namespace Modules.Wms.Infrastructure.Persistence;

internal sealed class LotRepository(WmsDbContext dbContext) : ILotRepository
{
    public void Add(Lot lot) => dbContext.Set<Lot>().Add(lot);

    public Task<Lot?> FindAsync(Guid tenantId, Guid ownerId, Guid variantId, string code, CancellationToken ct) =>
        dbContext.Set<Lot>().FirstOrDefaultAsync(
            lot => lot.TenantId == tenantId && lot.OwnerId == ownerId && lot.VariantId == variantId && lot.Code == code,
            ct);

    public async Task<IReadOnlyDictionary<Guid, Lot>> GetManyAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, Lot>();
        }

        var wanted = ids.Select(id => new LotId(id)).ToArray();
        var lots = await dbContext.Set<Lot>()
            .Where(lot => lot.TenantId == tenantId && wanted.Contains(lot.Id))
            .ToDictionaryAsync(lot => lot.Id.Value, ct);

        // La carga inicial crea los lotes que faltan y postea en la misma transacción: uno recién agregado todavía no está en la base.
        foreach (var lot in dbContext.Set<Lot>().Local.Where(lot => lot.TenantId == tenantId && ids.Contains(lot.Id.Value)))
        {
            lots.TryAdd(lot.Id.Value, lot);
        }

        return lots;
    }
}
