namespace Modules.Geography.Domain;

public readonly record struct RegionCityId(Guid Value)
{
    public static RegionCityId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
