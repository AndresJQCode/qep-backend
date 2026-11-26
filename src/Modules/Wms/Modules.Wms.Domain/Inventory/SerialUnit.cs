namespace Modules.Wms.Domain.Inventory;

/// <summary>Una unidad con número de serie. Su ubicación y estado actuales los mueve el ledger.</summary>
public sealed class SerialUnit
{
    private SerialUnit()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid OwnerId { get; private set; }

    public Guid VariantId { get; private set; }

    public string Serial { get; private set; } = string.Empty;

    public Guid CurrentLocationId { get; private set; }

    public StockStatus Status { get; private set; }

    public LotId? LotId { get; private set; }

    internal void MoveTo(Guid locationId, StockStatus status)
    {
        CurrentLocationId = locationId;
        Status = status;
    }
}
