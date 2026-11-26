using Modules.Wms.Domain.Control;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.Application;

/// <summary>De un ajuste aprobado a los movimientos del ledger. La ubicación virtual <c>ADJUSTMENT</c> hace de contrapartida: un delta
/// positivo trae stock desde ella y uno negativo lo devuelve, con el mismo estado de saldo de cada lado (el ajuste cambia cuánto hay,
/// no en qué estado está).</summary>
public static class AdjustmentPosting
{
    public static IReadOnlyList<LedgerEntry> EntriesFor(
        Adjustment adjustment, Location adjustmentLocation, DateTimeOffset occurredAt, MovementReason reason)
    {
        ArgumentNullException.ThrowIfNull(adjustment);
        ArgumentNullException.ThrowIfNull(adjustmentLocation);

        var entries = new List<LedgerEntry>(adjustment.Lines.Count);
        foreach (var line in adjustment.Lines)
        {
            var quantity = Math.Abs(line.QuantityDelta);
            var lotId = line.LotId?.Value;
            var onLocation = new StockKey(adjustment.OwnerId.Value, line.LocationId.Value, line.VariantId, lotId, line.Status);
            var onAdjustment = new StockKey(adjustment.OwnerId.Value, adjustmentLocation.Id.Value, line.VariantId, lotId, line.Status);

            // El factor sale de la captura (24 = 2 CAJA × 12): el ledger vuelve a comprobarlo contra el perfil de la variante.
            var captured = line.CapturedUom is not null && line.CapturedQty is { } capturedQty
                ? new LedgerCapture(line.CapturedUom, capturedQty, quantity / capturedQty)
                : null;

            entries.Add(new LedgerEntry(
                line.QuantityDelta > 0 ? onAdjustment : onLocation,
                line.QuantityDelta > 0 ? onLocation : onAdjustment,
                quantity,
                captured,
                reason,
                new LedgerDocument("Adjustment", adjustment.Id, line.Id),
                occurredAt));
        }

        return entries;
    }
}
