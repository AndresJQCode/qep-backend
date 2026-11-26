namespace Modules.Payroll.Domain;

public readonly record struct CompanyPayrollSettingsId(Guid Value)
{
    public static CompanyPayrollSettingsId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
