namespace Modules.Payroll.Domain;

public readonly record struct PayrollConceptId(Guid Value)
{
    public static PayrollConceptId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
