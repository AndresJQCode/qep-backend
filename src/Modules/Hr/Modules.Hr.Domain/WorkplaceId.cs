namespace Modules.Hr.Domain;

public readonly record struct WorkplaceId(Guid Value)
{
    public static WorkplaceId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
