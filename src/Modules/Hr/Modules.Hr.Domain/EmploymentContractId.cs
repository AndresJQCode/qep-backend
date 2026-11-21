namespace Modules.Hr.Domain;

public readonly record struct EmploymentContractId(Guid Value)
{
    public static EmploymentContractId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
