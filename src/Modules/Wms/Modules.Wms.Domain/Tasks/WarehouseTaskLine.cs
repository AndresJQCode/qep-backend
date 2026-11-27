using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.Domain.Tasks;

/// <summary>Una línea de tarea: cuánto de una variante hay que llevar de una ubicación a otra. Las cantidades van en unidad base.
/// Sólo la cambia su tarea.</summary>
public sealed class WarehouseTaskLine
{
    private WarehouseTaskLine()
    {
    }

    public Guid Id { get; private set; }

    public Guid VariantId { get; private set; }

    /// <summary><c>null</c>: la línea no fija lote.</summary>
    public LotId? LotId { get; private set; }

    public StockStatus StockStatus { get; private set; }

    public LocationId FromLocationId { get; private set; }

    /// <summary><c>null</c>: el operario elige destino, sólo en los tipos que lo admiten.</summary>
    public LocationId? ToLocationId { get; private set; }

    public decimal QuantityPlanned { get; private set; }

    public decimal QuantityConfirmed { get; private set; }

    /// <summary>Lo que el operario contó, sólo en la línea de un <c>Count</c>; <c>null</c> mientras no cuenta. Cero es una cuenta
    /// válida.</summary>
    public decimal? QuantityCounted { get; private set; }

    public TaskLineStatus Status { get; private set; }

    public string? CancelReason { get; private set; }

    public int Position { get; private set; }

    public decimal Remaining => QuantityPlanned - QuantityConfirmed;

    public static WarehouseTaskLine Create(
        Guid variantId, LotId? lotId, StockStatus stockStatus, LocationId from, LocationId? to, decimal quantity)
    {
        if (quantity <= 0)
        {
            throw new WmsDomainException(WmsErrorCodes.TaskLineInvalid, "La cantidad de una línea de tarea tiene que ser mayor que cero.");
        }

        if (to == from)
        {
            throw new WmsDomainException(WmsErrorCodes.TaskLocationInvalid, "El origen y el destino de una línea no pueden ser el mismo.");
        }

        return new WarehouseTaskLine
        {
            Id = Guid.CreateVersion7(),
            VariantId = variantId,
            LotId = lotId,
            StockStatus = stockStatus,
            FromLocationId = from,
            ToLocationId = to,
            QuantityPlanned = quantity,
            Status = TaskLineStatus.Open,
        };
    }

    /// <summary>La línea de un conteo: una clave sin destino y con lo planeado en cero. Lo esperado no vive en la línea: el servidor lo
    /// calcula al contar, y así nada que llegue al operario lo revela (DA-39).</summary>
    public static WarehouseTaskLine CreateCount(Guid variantId, LotId? lotId, StockStatus stockStatus, LocationId location) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            VariantId = variantId,
            LotId = lotId,
            StockStatus = stockStatus,
            FromLocationId = location,
            ToLocationId = null,
            QuantityPlanned = 0,
            Status = TaskLineStatus.Open,
        };

    internal void Place(int position) => Position = position;

    /// <summary>La tarea ya validó la línea abierta, la cuenta y el lote. El lote escaneado completa una línea que no fijaba lote.</summary>
    internal void RecordCount(decimal counted, LotId? lot)
    {
        QuantityCounted = counted;
        LotId ??= lot;
        Status = TaskLineStatus.Done;
    }

    /// <summary>La tarea ya validó que la línea está abierta y que la cantidad no supera lo pendiente.</summary>
    internal void Confirm(decimal quantity)
    {
        QuantityConfirmed += quantity;
        if (Remaining == 0)
        {
            Status = TaskLineStatus.Done;
        }
    }

    /// <summary>De lo que faltaba sólo había una parte: la línea se cierra sin completarse. Conserva lo confirmado y ya no admite
    /// más confirmaciones.</summary>
    internal void MarkShort() => Status = TaskLineStatus.Short;

    internal void Cancel(string reason)
    {
        Status = TaskLineStatus.Cancelled;
        CancelReason = reason;
    }
}
