namespace Modules.Scheduling.Domain;

public readonly record struct ServiceId(Guid Value)
{
    public static ServiceId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
