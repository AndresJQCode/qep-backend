namespace Modules.Payroll.Domain;

public readonly record struct PeriodEventId(Guid Value)
{
    public static PeriodEventId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
