using Modules.Hr.Domain;
using static Modules.Hr.UnitTests.HrTestData;

namespace Modules.Hr.UnitTests;

public sealed class ContractRecordsTests
{
    private static readonly DateTimeOffset Later = Now.AddMinutes(5);

    private static void AddSalary(Employee employee, DateOnly date, decimal amount) =>
        employee.AddSalary(
            employee.ActiveContract!.Id, SalaryRecordId.New(), new SalaryEntry(date, amount, false, false), Floor2026, null, Later);

    private static void AddAssignment(Employee employee, DateOnly date, PositionId position, Workplace workplace) =>
        employee.AddAssignment(
            employee.ActiveContract!.Id,
            AssignmentRecordId.New(),
            new AssignmentEntry(date, position, null, workplace.SiteId, workplace.Id),
            null,
            Later);

    [Fact]
    public void ASalaryMustBeAfterTheLastOne()
    {
        var employee = Hire();

        var exception = Rejects(() => AddSalary(employee, new DateOnly(2026, 1, 15), 2_600_000m));

        Assert.Equal("hr.salary.effective_date_not_after_last", exception.Code);
    }

    [Fact]
    public void ASalaryRaiseIsANewRecord()
    {
        var employee = Hire();

        AddSalary(employee, new DateOnly(2026, 12, 1), 2_800_000m);

        var salaries = employee.ActiveContract!.Salaries;
        Assert.Equal(2, salaries.Count);
        Assert.Contains(salaries, salary => salary.EffectiveDate == new DateOnly(2026, 1, 15) && salary.Amount == 2_500_000m);
        Assert.Contains(salaries, salary => salary.EffectiveDate == new DateOnly(2026, 12, 1) && salary.Amount == 2_800_000m);
    }

    [Fact]
    public void RewritingWithoutConfirmationIsRejected()
    {
        var employee = Hire();
        var salary = employee.ActiveContract!.Salaries.Single();

        var exception = Rejects(() => employee.RewriteSalary(
            employee.ActiveContract.Id, salary.Id, 2_600_000m, false, false, false, Floor2026, Later));

        Assert.Equal("hr.salary.rewrite_unconfirmed", exception.Code);
    }

    [Fact]
    public void RewritingKeepsTheEffectiveDate()
    {
        var employee = Hire();
        var salary = employee.ActiveContract!.Salaries.Single();

        employee.RewriteSalary(employee.ActiveContract.Id, salary.Id, 2_600_000m, true, false, true, Floor2026, Later);

        Assert.Equal(new DateOnly(2026, 1, 15), salary.EffectiveDate);
        Assert.Equal(2_600_000m, salary.Amount);
        Assert.True(salary.Teleworker);
        Assert.Equal(Later, salary.UpdatedAt);
    }

    [Fact]
    public void RewritingBelowTheIntegralFloorIsRejected()
    {
        var employee = Hire(Contract(Terms(salaryType: SalaryType.Integral), amount: 23_000_000m));
        var salary = employee.ActiveContract!.Salaries.Single();

        var exception = Rejects(() => employee.RewriteSalary(
            employee.ActiveContract.Id, salary.Id, 20_000_000m, false, true, true, Floor2026, Later));

        Assert.Equal("hr.salary.below_integral_floor", exception.Code);
    }

    [Fact]
    public void AnUnknownSalaryIsNotFound()
    {
        var employee = Hire();

        var exception = Rejects(() => employee.RewriteSalary(
            employee.ActiveContract!.Id, SalaryRecordId.New(), 2_600_000m, false, false, true, Floor2026, Later));

        Assert.Equal("hr.salary.not_found", exception.Code);
    }

    [Fact]
    public void AnAssignmentMustBeAfterTheLastOne()
    {
        var employee = Hire();

        var exception = Rejects(() => AddAssignment(employee, new DateOnly(2026, 1, 15), ManagerId, Warehouse));

        Assert.Equal("hr.assignment.effective_date_not_after_last", exception.Code);
    }

    [Fact]
    public void AWorkplaceChangeOffTheFirstIsRejected()
    {
        var employee = Hire();

        var exception = Rejects(() => AddAssignment(employee, new DateOnly(2026, 12, 15), AnalystId, OtherWarehouse));

        Assert.Equal("hr.assignment.site_change_not_first_of_month", exception.Code);
    }

    [Fact]
    public void AWorkplaceChangeOnTheFirstIsAccepted()
    {
        var employee = Hire();

        AddAssignment(employee, new DateOnly(2026, 12, 1), AnalystId, OtherWarehouse);

        Assert.Equal(2, employee.ActiveContract!.Assignments.Count);
    }

    [Fact]
    public void APositionOnlyChangeMayBeAnyDay()
    {
        var employee = Hire();

        AddAssignment(employee, new DateOnly(2026, 12, 15), ManagerId, Warehouse);

        Assert.Equal(2, employee.ActiveContract!.Assignments.Count);
    }

    [Fact]
    public void ANoOpAssignmentIsRejected()
    {
        var employee = Hire();

        var exception = Rejects(() => AddAssignment(employee, new DateOnly(2026, 12, 15), AnalystId, Warehouse));

        Assert.Equal("hr.assignment.unchanged", exception.Code);
    }

    [Fact]
    public void RecordsOnANonActiveContractAreRejected()
    {
        var employee = Hire();
        var id = employee.ActiveContract!.Id;
        employee.Terminate(id, TerminationReason.Resignation, new DateOnly(2026, 11, 30), null, Later);

        Assert.Equal("hr.contract.not_active", Rejects(() => employee.AddSalary(
            id, SalaryRecordId.New(), new SalaryEntry(new DateOnly(2026, 12, 1), 2_800_000m, false, false), Floor2026, null, Later)).Code);
        Assert.Equal("hr.contract.not_active", Rejects(() => employee.AddAssignment(
            id, AssignmentRecordId.New(), new AssignmentEntry(new DateOnly(2026, 12, 1), ManagerId, null, Warehouse.SiteId, Warehouse.Id), null, Later)).Code);
        Assert.Equal("hr.contract.not_active", Rejects(() => employee.Renew(
            id, ContractRenewalId.New(), new DateOnly(2027, 1, 14), null, Later)).Code);
        Assert.Equal("hr.contract.not_active", Rejects(() => employee.ConvertToIndefinite(id, Today, Later)).Code);
    }

    [Fact]
    public void TheFourthShortRenewalMustLastAYear()
    {
        var employee = Hire();
        var id = employee.ActiveContract!.Id;

        employee.Renew(id, ContractRenewalId.New(), new DateOnly(2027, 1, 14), null, Later);
        employee.Renew(id, ContractRenewalId.New(), new DateOnly(2027, 7, 14), null, Later);
        employee.Renew(id, ContractRenewalId.New(), new DateOnly(2028, 1, 14), null, Later);

        Assert.Equal(
            "hr.contract.renewal_minimum_one_year",
            Rejects(() => employee.Renew(id, ContractRenewalId.New(), new DateOnly(2028, 7, 14), null, Later)).Code);
        var renewal = employee.Renew(id, ContractRenewalId.New(), new DateOnly(2029, 1, 14), null, Later);
        Assert.False(renewal.IsShort);
        Assert.Equal(new DateOnly(2028, 1, 14), renewal.PreviousEndDate);
        Assert.Equal(new DateOnly(2029, 1, 14), employee.ActiveContract!.EndDate);
    }

    [Fact]
    public void ARenewalPastTheCapIsRejected()
    {
        var employee = Hire();
        var id = employee.ActiveContract!.Id;

        Assert.Equal(
            "hr.contract.fixed_term_cap_exceeded",
            Rejects(() => employee.Renew(id, ContractRenewalId.New(), new DateOnly(2030, 1, 15), null, Later)).Code);
        employee.Renew(id, ContractRenewalId.New(), new DateOnly(2030, 1, 14), null, Later);
    }

    [Fact]
    public void ARenewalNotAfterTheCurrentEndIsOutOfRange()
    {
        var employee = Hire();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            employee.Renew(employee.ActiveContract!.Id, ContractRenewalId.New(), new DateOnly(2026, 7, 14), null, Later));
    }

    [Theory]
    [InlineData(ContractDuration.Indefinite)]
    [InlineData(ContractDuration.WorkOrLabor)]
    public void OnlyFixedTermRenews(ContractDuration duration)
    {
        var employee = Hire(Contract(Terms(duration: duration, end: new DateOnly(2026, 12, 31))));

        var exception = Rejects(() =>
            employee.Renew(employee.ActiveContract!.Id, ContractRenewalId.New(), new DateOnly(2027, 6, 30), null, Later));

        Assert.Equal("hr.contract.not_fixed_term", exception.Code);
    }

    [Fact]
    public void ConvertingToIndefiniteKeepsTheStartAndClearsTheEnd()
    {
        var employee = Hire();
        var contract = employee.ActiveContract!;

        employee.ConvertToIndefinite(contract.Id, Today, Later);

        Assert.Equal(ContractDuration.Indefinite, contract.Duration);
        Assert.Null(contract.EndDate);
        Assert.Equal(Today, contract.ConvertedToIndefiniteOn);
        Assert.Equal(new DateOnly(2026, 1, 15), contract.StartDate);
    }

    [Fact]
    public void DocumentsCanBeSetInAnyStateAndCleared()
    {
        var employee = Hire();
        var contract = employee.ActiveContract!;
        employee.Terminate(contract.Id, TerminationReason.Resignation, new DateOnly(2026, 11, 30), null, Later);
        var file = new DocumentRef(Guid.NewGuid(), "doc.pdf");

        employee.SetSalaryDocument(contract.Id, contract.Salaries.Single().Id, file, Later);
        employee.SetAssignmentDocument(contract.Id, contract.Assignments.Single().Id, file, Later);
        employee.SetContractDocument(contract.Id, file, Later);

        Assert.Equal(file.FileId, contract.Salaries.Single().DocumentFileId);
        Assert.Equal(file.FileId, contract.Assignments.Single().DocumentFileId);
        Assert.Equal("doc.pdf", contract.DocumentFileName);

        employee.SetContractDocument(contract.Id, null, Later);
        Assert.Null(contract.DocumentFileId);
        Assert.Equal(
            "hr.renewal.not_found",
            Rejects(() => employee.SetRenewalDocument(contract.Id, ContractRenewalId.New(), file, Later)).Code);
        Assert.Equal(
            "hr.assignment.not_found",
            Rejects(() => employee.SetAssignmentDocument(contract.Id, AssignmentRecordId.New(), file, Later)).Code);
    }
}
