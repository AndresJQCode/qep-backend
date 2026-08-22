namespace Modules.Geography.Domain;

public readonly record struct DepartmentId(Guid Value)
{
    public static DepartmentId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
