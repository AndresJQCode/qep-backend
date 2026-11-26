namespace Modules.Wms.Domain.Warehousing;

public readonly record struct WarehouseId(Guid Value)
{
    public static WarehouseId New() => new(Guid.CreateVersion7());
}
