using Modules.Wms.Application;

namespace Modules.Wms.Infrastructure.Persistence;

// WMS-B no tiene libro de inventario, así que nada tiene existencias y ninguna regla «no se puede mientras haya stock» se dispara.
// WMS-D reemplaza las tres por consultas al libro; las pruebas las sustituyen con configureServices para ejercer esas reglas.

internal sealed class NoVariantStockProbe : IVariantStockProbe
{
    public Task<bool> HasStockAsync(Guid tenantId, Guid variantId, CancellationToken ct) => Task.FromResult(false);
}

internal sealed class NoLocationStockProbe : ILocationStockProbe
{
    public Task<bool> HasStockAsync(Guid tenantId, Guid locationId, CancellationToken ct) => Task.FromResult(false);
}

internal sealed class NoOwnerStockProbe : IOwnerStockProbe
{
    public Task<bool> HasStockAsync(Guid tenantId, Guid ownerId, CancellationToken ct) => Task.FromResult(false);
}
