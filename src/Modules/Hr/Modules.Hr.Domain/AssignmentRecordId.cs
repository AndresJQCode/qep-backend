namespace Modules.Hr.Domain;

public readonly record struct AssignmentRecordId(Guid Value)
{
    public static AssignmentRecordId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
