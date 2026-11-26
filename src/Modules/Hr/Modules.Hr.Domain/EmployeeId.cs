namespace Modules.Hr.Domain;

public readonly record struct EmployeeId(Guid Value)
{
    public static EmployeeId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
