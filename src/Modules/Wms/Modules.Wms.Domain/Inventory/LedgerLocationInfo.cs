namespace Modules.Wms.Domain.Inventory;

/// <summary>Lo que el ledger necesita de una ubicación, sin conocer <c>Location</c>. <c>AllowsNegative</c> es
/// <c>Location.AllowsNegative</c> (S1).</summary>
public sealed record LedgerLocationInfo(Guid Id, Guid WarehouseId, string Code, bool AllowsNegative, bool IsBlocked, bool IsCounting);
