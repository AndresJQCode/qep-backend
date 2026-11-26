namespace Modules.Hr.Domain;

public readonly record struct ActivityEntryId(Guid Value)
{
    public static ActivityEntryId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
