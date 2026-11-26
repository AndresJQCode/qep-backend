namespace Modules.Scheduling.Domain;

public readonly record struct ResourceId(Guid Value)
{
    public static ResourceId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
