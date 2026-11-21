namespace Modules.Hr.Domain;

public readonly record struct AreaId(Guid Value)
{
    public static AreaId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
