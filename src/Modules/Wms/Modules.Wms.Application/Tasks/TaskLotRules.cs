using BuildingBlocks.Application;
using Modules.Wms.Domain;

namespace Modules.Wms.Application;

/// <summary>La FK de una línea o de una novedad al lote no mira el tenant, el dueño ni la variante: esta es la regla que aplica el
/// ledger al confirmar (404 <c>wms.lot.not_found</c> salvo que coincidan los tres), para los caminos que no llaman al ledger.</summary>
internal static class TaskLotRules
{
    public static async Task EnsureLotsAsync(
        ILotRepository lots,
        Guid tenantId,
        Guid ownerId,
        IReadOnlyCollection<(Guid VariantId, Guid LotId)> wanted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lots);
        ArgumentNullException.ThrowIfNull(wanted);
        if (wanted.Count == 0)
        {
            return;
        }

        var found = await lots.GetManyAsync(tenantId, [.. wanted.Select(value => value.LotId).Distinct()], cancellationToken);
        foreach (var (variantId, lotId) in wanted)
        {
            if (!found.TryGetValue(lotId, out var lot) || lot.VariantId != variantId || lot.OwnerId != ownerId)
            {
                throw new ResourceNotFoundException(WmsErrorCodes.LotNotFound, $"Lot '{lotId}' was not found.");
            }
        }
    }

    public static Task EnsureLotAsync(
        ILotRepository lots, Guid tenantId, Guid ownerId, Guid variantId, Guid lotId, CancellationToken cancellationToken) =>
        EnsureLotsAsync(lots, tenantId, ownerId, [(variantId, lotId)], cancellationToken);
}
