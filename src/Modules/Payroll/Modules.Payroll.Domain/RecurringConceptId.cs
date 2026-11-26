namespace Modules.Payroll.Domain;

public readonly record struct RecurringConceptId(Guid Value)
{
    public static RecurringConceptId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
