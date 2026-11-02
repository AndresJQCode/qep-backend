using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.Domain.Control;

/// <summary>Una línea de ajuste: cuánto sube o baja el saldo de una clave. Sólo la forma persistida; el comportamiento llega con
/// <c>Adjustment.Propose</c> (Task 8).</summary>
public sealed class AdjustmentLine
{
    private AdjustmentLine()
    {
    }

    public Guid Id { get; private set; }

    public LocationId LocationId { get; private set; }

    public Guid VariantId { get; private set; }

    public LotId? LotId { get; private set; }

    public StockStatus Status { get; private set; }

    public decimal QuantityDelta { get; private set; }

    public string? CapturedUom { get; private set; }

    public decimal? CapturedQty { get; private set; }

    public int Position { get; private set; }
}
