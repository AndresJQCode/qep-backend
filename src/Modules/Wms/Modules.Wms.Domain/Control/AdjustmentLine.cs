using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.Domain.Control;

/// <summary>Una línea de ajuste: cuánto sube o baja el saldo de una clave.</summary>
public sealed class AdjustmentLine
{
    private AdjustmentLine()
    {
    }

    public const int CapturedUomMaxLength = 32;

    public Guid Id { get; private set; }

    public LocationId LocationId { get; private set; }

    public Guid VariantId { get; private set; }

    public LotId? LotId { get; private set; }

    public StockStatus Status { get; private set; }

    public decimal QuantityDelta { get; private set; }

    public string? CapturedUom { get; private set; }

    public decimal? CapturedQty { get; private set; }

    public int Position { get; private set; }

    /// <summary>Un delta cero no mueve nada, y una captura va completa (presentación y cantidad) o no va: el factor que el ledger
    /// valida se deduce de las dos.</summary>
    public static AdjustmentLine Create(
        LocationId locationId,
        Guid variantId,
        LotId? lotId,
        StockStatus status,
        decimal quantityDelta,
        string? capturedUom,
        decimal? capturedQty)
    {
        if (quantityDelta == 0)
        {
            throw new WmsDomainException(WmsErrorCodes.AdjustmentLineInvalid, "La cantidad de una línea de ajuste no puede ser cero.");
        }

        var captured = !string.IsNullOrWhiteSpace(capturedUom);
        if (captured != capturedQty.HasValue || (capturedQty is { } qty && qty <= 0))
        {
            throw new WmsDomainException(
                WmsErrorCodes.AdjustmentLineInvalid,
                "La captura de una línea lleva la presentación y una cantidad mayor que cero, o ninguna de las dos.");
        }

        return new AdjustmentLine
        {
            Id = Guid.CreateVersion7(),
            LocationId = locationId,
            VariantId = variantId,
            LotId = lotId,
            Status = status,
            QuantityDelta = quantityDelta,
            CapturedUom = captured ? capturedUom!.Trim() : null,
            CapturedQty = captured ? capturedQty : null,
        };
    }

    internal void Place(int position) => Position = position;
}
