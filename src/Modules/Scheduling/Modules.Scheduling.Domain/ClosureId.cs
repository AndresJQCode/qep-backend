namespace Modules.Scheduling.Domain;

public readonly record struct ClosureId(Guid Value)
{
    public static ClosureId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
