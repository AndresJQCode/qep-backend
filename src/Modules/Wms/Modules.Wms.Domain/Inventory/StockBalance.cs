namespace Modules.Wms.Domain.Inventory;

/// <summary>
/// Saldo por clave (<c>wms.stock_balances</c>). Sólo lectura: lo escribe el ledger por SQL (upsert por clave) y nadie la instancia
/// fuera de EF. <see cref="AllowsNegative"/> es la copia de <c>Location.AllowsNegative</c> que el ledger deja en el upsert (S1),
/// para que el CHECK de la base no necesite un join.
/// </summary>
public sealed class StockBalance
{
    private StockBalance()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid OwnerId { get; private set; }

    public Guid WarehouseId { get; private set; }

    public Guid LocationId { get; private set; }

    public Guid VariantId { get; private set; }

    public Guid? LotId { get; private set; }

    /// <summary>D-12: la caja o el palet que contiene el saldo; <c>null</c> es suelto. Parte de la clave única.</summary>
    public Guid? HandlingUnitId { get; private set; }

    public StockStatus Status { get; private set; }

    public decimal OnHand { get; private set; }

    public decimal Reserved { get; private set; }

    public long Version { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public bool AllowsNegative { get; private set; }
}
