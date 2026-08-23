namespace Modules.Geography.Domain;

public readonly record struct CityId(Guid Value)
{
    public static CityId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
