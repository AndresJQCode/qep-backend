namespace Modules.Wms.Domain.Warehousing;

public readonly record struct LocationId(Guid Value)
{
    public static LocationId New() => new(Guid.CreateVersion7());
}
