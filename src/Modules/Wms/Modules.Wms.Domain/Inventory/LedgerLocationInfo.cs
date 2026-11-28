namespace Modules.Wms.Domain.Inventory;

/// <summary>Lo que el ledger necesita de una ubicación, sin conocer <c>Location</c>. <c>AllowsNegative</c> es
/// <c>Location.AllowsNegative</c> (S1). <c>IsVirtual</c> distingue el tránsito, que es virtual y aun así no admite negativo, de una
/// ubicación física: sólo en la física puede una venta dejar el saldo bajo cero.</summary>
public sealed record LedgerLocationInfo(
    Guid Id, Guid WarehouseId, string Code, bool AllowsNegative, bool IsBlocked, bool IsCounting, bool IsVirtual = false);
