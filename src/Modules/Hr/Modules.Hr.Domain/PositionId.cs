namespace Modules.Hr.Domain;

public readonly record struct PositionId(Guid Value)
{
    public static PositionId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
