using Modules.Hr.Domain;
using static Modules.Hr.UnitTests.HrTestData;

namespace Modules.Hr.UnitTests;

public sealed class ContractConversionTests
{
    private static readonly DateTimeOffset Later = Now.AddMinutes(5);

    private static Employee Apprentice() => Hire(Contract(
        Terms(ContractType.SenaApprenticeTeaching, end: new DateOnly(2026, 12, 31)), amount: 1_000_000m));

    private static NewContract Productive(
        DateOnly? start = null, Guid? companyId = null, SalaryType salaryType = SalaryType.Monthly, decimal amount = 1_750_905m) =>
        Contract(
            Terms(
                ContractType.SenaApprenticeProductive,
                start: start ?? new DateOnly(2026, 7, 1),
                end: new DateOnly(2027, 6, 30),
                salaryType: salaryType,
                companyId: companyId),
            amount: amount);

    [Fact]
    public void AConversionClosesThePreviousTheDayBefore()
    {
        var employee = Apprentice();
        var previous = employee.ActiveContract!;
        var next = Productive();

        employee.ChangeContractType(previous.Id, next, Floor2026, Later);

        Assert.Equal(ContractStatus.Converted, previous.Status);
        Assert.Equal(new DateOnly(2026, 6, 30), previous.EndedOn);
        var created = employee.ActiveContract!;
        Assert.Equal(ContractStatus.Active, created.Status);
        Assert.Equal(previous.Id, created.PreviousContractId);
        Assert.Equal(new DateOnly(2026, 7, 1), Assert.Single(created.Salaries).EffectiveDate);
        Assert.Equal(new DateOnly(2026, 7, 1), Assert.Single(created.Assignments).EffectiveDate);
        Assert.Equal(EmployeeStatus.Active, employee.Status);
    }

    [Fact]
    public void AConversionStartingOnOrBeforeTheCurrentStartIsRejected()
    {
        var employee = Apprentice();

        var exception = Rejects(() => employee.ChangeContractType(
            employee.ActiveContract!.Id, Productive(start: new DateOnly(2026, 1, 15)), Floor2026, Later));

        Assert.Equal("hr.contract.start_not_after_previous", exception.Code);
    }

    [Fact]
    public void AConversionWithARecordAfterTheCloseIsRejected()
    {
        var employee = Apprentice();
        var id = employee.ActiveContract!.Id;
        employee.AddSalary(
            id, SalaryRecordId.New(), new SalaryEntry(new DateOnly(2026, 8, 1), 1_100_000m, false, false), Floor2026, null, Later);

        var exception = Rejects(() => employee.ChangeContractType(id, Productive(), Floor2026, Later));

        Assert.Equal("hr.contract.records_after_end", exception.Code);
        Assert.Equal(ContractStatus.Active, employee.ActiveContract!.Status);
    }

    [Fact]
    public void IntegralNeedsTheFloorAtTheNewStart()
    {
        var employee = Hire();
        var next = Contract(Terms(salaryType: SalaryType.Integral, start: new DateOnly(2026, 7, 1), end: new DateOnly(2027, 6, 30)), amount: 20_000_000m, acknowledge: true);

        var exception = Rejects(() => employee.ChangeContractType(employee.ActiveContract!.Id, next, Floor2026, Later));

        Assert.Equal("hr.salary.below_integral_floor", exception.Code);
        Assert.Equal(ContractStatus.Active, employee.ActiveContract!.Status);
    }

    [Fact]
    public void AConversionCannotChangeTheCompany()
    {
        var employee = Apprentice();
        var current = employee.ActiveContract!;

        var exception = Rejects(() => employee.ChangeContractType(
            current.Id, Productive(companyId: OtherCompanyId), Floor2026, Later));

        Assert.Equal("hr.contract.company_change_not_allowed", exception.Code);
        Assert.Equal(ContractStatus.Active, current.Status);
        Assert.Single(employee.Contracts);
    }

    [Fact]
    public void AConversionKeepsTheCompany()
    {
        var employee = Apprentice();

        employee.ChangeContractType(employee.ActiveContract!.Id, Productive(companyId: CompanyId), Floor2026, Later);

        Assert.Equal(CompanyId, employee.ActiveContract!.CompanyId);
        Assert.Equal(2, employee.Contracts.Count);
    }
}
