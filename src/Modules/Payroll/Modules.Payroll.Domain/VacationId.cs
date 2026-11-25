namespace Modules.Payroll.Domain;

public readonly record struct VacationId(Guid Value)
{
    public static VacationId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
