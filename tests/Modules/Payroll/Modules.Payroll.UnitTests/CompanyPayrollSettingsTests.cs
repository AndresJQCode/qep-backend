using Modules.Payroll.Domain;
using Modules.Payroll.Domain.Engine;

namespace Modules.Payroll.UnitTests;

public sealed class CompanyPayrollSettingsTests
{
    // Decision 2 (owner, 2026-11-22, option A): the transport cap counts salary income by default.
    [Fact]
    public void DefaultsCountSalaryIncomeTowardTheTransportCap() =>
        Assert.Equal(TransportCapBase.SalaryAndSalaryIncome, CompanyPayrollSettings.DefaultsFor().TransportCapBase);
}
