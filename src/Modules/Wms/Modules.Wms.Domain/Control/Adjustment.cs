using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.Domain.Control;

/// <summary>Un ajuste manual de inventario. Sólo la forma persistida: el comportamiento (proponer, aprobar, contabilizar, rechazar)
/// llega en Task 8.</summary>
public sealed class Adjustment
{
    private readonly List<AdjustmentLine> _lines = [];

    private Adjustment()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public OwnerId OwnerId { get; private set; }

    public WarehouseId WarehouseId { get; private set; }

    public string Number { get; private set; } = string.Empty;

    public AdjustmentReason Reason { get; private set; }

    public AdjustmentStatus Status { get; private set; }

    public string? Note { get; private set; }

    /// <summary>S12: la fecha física del ajuste. <c>null</c> es la hora del <c>post</c>.</summary>
    public DateTimeOffset? OccurredAt { get; private set; }

    public Guid ProposedBy { get; private set; }

    public DateTimeOffset ProposedAt { get; private set; }

    public Guid? ApprovedBy { get; private set; }

    public DateTimeOffset? ApprovedAt { get; private set; }

    public DateTimeOffset? PostedAt { get; private set; }

    public DateTimeOffset? RejectedAt { get; private set; }

    public Guid? RejectedBy { get; private set; }

    public string? RejectNote { get; private set; }

    public Guid? BatchId { get; private set; }

    public long Version { get; private set; }

    public IReadOnlyList<AdjustmentLine> Lines => _lines;
}
