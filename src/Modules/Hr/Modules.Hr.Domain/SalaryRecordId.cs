namespace Modules.Hr.Domain;

public readonly record struct SalaryRecordId(Guid Value)
{
    public static SalaryRecordId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
