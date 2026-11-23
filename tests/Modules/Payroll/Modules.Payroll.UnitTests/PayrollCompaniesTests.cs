using Modules.Payroll.Application;
using Modules.Payroll.Domain;

namespace Modules.Payroll.UnitTests;

public sealed class PayrollCompaniesTests
{
    private static PayrollCompanyRef Company(bool active) =>
        new(Guid.Parse("01900000-0000-7000-8000-0000000000c1"), "Comercializadora QEP S.A.S.", "900123456", active);

    [Fact]
    public void AnInactiveCompanyIsCompanyInactive()
    {
        var exception = Assert.Throws<PayrollDomainException>(() => PayrollCompanies.EnsureActive(Company(false), "No."));

        Assert.Equal(PayrollErrorCodes.CompanyInactive, exception.Code);
    }

    [Fact]
    public void AnActiveCompanyPasses() => PayrollCompanies.EnsureActive(Company(true), "No.");
}
