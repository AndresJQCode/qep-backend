namespace Modules.Wms.Domain.Warehousing;

public readonly record struct OwnerId(Guid Value)
{
    public static OwnerId New() => new(Guid.CreateVersion7());
}
