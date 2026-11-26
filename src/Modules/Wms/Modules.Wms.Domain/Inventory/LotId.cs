namespace Modules.Wms.Domain.Inventory;

public readonly record struct LotId(Guid Value)
{
    public static LotId New() => new(Guid.CreateVersion7());
}
