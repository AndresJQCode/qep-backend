using Modules.Wms.Domain.Inventory;

namespace Modules.Wms.Application;

public interface ILotRepository
{
    Task<Lot?> FindAsync(Guid tenantId, Guid ownerId, Guid variantId, string code, CancellationToken ct);

    /// <summary>Por id, incluidos los lotes agregados con <see cref="Add"/> que todavía no se guardaron. Los ids que no existen no
    /// aparecen.</summary>
    Task<IReadOnlyDictionary<Guid, Lot>> GetManyAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken ct);

    void Add(Lot lot);
}
