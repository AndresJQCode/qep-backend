using Modules.Wms.Domain.Inventory;

namespace Modules.Wms.Domain.Control;

/// <summary>Una diferencia entre el ledger y el saldo, detectada por la conciliación. No se corrige sola: queda escrita para que
/// una persona la resuelva.</summary>
public sealed class BalanceCheck
{
    private BalanceCheck()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid WarehouseId { get; private set; }

    public Guid OwnerId { get; private set; }

    public Guid LocationId { get; private set; }

    public Guid VariantId { get; private set; }

    public Guid? LotId { get; private set; }

    public Guid? HandlingUnitId { get; private set; }

    public StockStatus Status { get; private set; }

    public decimal LedgerOnHand { get; private set; }

    public decimal BalanceOnHand { get; private set; }

    public Guid? LastMovementId { get; private set; }

    public DateTimeOffset CheckedAt { get; private set; }
}
