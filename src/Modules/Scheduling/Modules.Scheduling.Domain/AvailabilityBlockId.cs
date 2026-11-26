namespace Modules.Scheduling.Domain;

public readonly record struct AvailabilityBlockId(Guid Value)
{
    public static AvailabilityBlockId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
