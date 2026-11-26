namespace Modules.Payroll.Domain;

public readonly record struct PayrollPeriodId(Guid Value)
{
    public static PayrollPeriodId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
