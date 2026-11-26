namespace Modules.Geography.Domain;

public readonly record struct RegionId(Guid Value)
{
    public static RegionId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
