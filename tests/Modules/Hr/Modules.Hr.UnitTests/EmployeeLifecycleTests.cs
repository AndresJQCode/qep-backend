using Modules.Hr.Domain;
using static Modules.Hr.UnitTests.HrTestData;

namespace Modules.Hr.UnitTests;

public sealed class EmployeeLifecycleTests
{
    private static readonly DateTimeOffset Later = Now.AddMinutes(5);

    private static void Terminate(Employee employee, string date = "2026-11-30") =>
        employee.Terminate(
            employee.ActiveContract!.Id, TerminationReason.Resignation, DateOnly.ParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), null, Later);

    [Fact]
    public void HiringStartsActiveAtVersionOne()
    {
        var employee = Hire();

        Assert.Equal(EmployeeStatus.Active, employee.Status);
        Assert.Equal(1, employee.Version);
        var contract = Assert.Single(employee.Contracts);
        Assert.Equal(ContractStatus.Active, contract.Status);
        Assert.Equal(CompanyId, contract.CompanyId);
        Assert.Single(contract.Salaries);
        Assert.Single(contract.Assignments);
    }

    [Fact]
    public void HiringAFixedTermPastTheCapIsRejected()
    {
        var exception = Rejects(() => Hire(Contract(Terms(end: new DateOnly(2030, 1, 15)))));

        Assert.Equal("hr.contract.fixed_term_cap_exceeded", exception.Code);
    }

    [Fact]
    public void HiringBelowTheMinimumWithoutAcknowledgementIsRejected()
    {
        var exception = Rejects(() => Hire(Contract(amount: 1_750_904m)));

        Assert.Equal("hr.salary.below_minimum_unacknowledged", exception.Code);
    }

    [Fact]
    public void TerminatingRetiresTheEmployee()
    {
        var employee = Hire();

        Terminate(employee);

        Assert.Equal(EmployeeStatus.Retired, employee.Status);
        var contract = Assert.Single(employee.Contracts);
        Assert.Equal(ContractStatus.Terminated, contract.Status);
        Assert.Equal(new DateOnly(2026, 11, 30), contract.EndedOn);
        Assert.Equal(TerminationReason.Resignation, contract.TerminationReason);
        Assert.Null(employee.ActiveContract);
    }

    [Fact]
    public void DeactivatingClosesTheContract()
    {
        var employee = Hire();

        employee.Deactivate(new DateOnly(2026, 11, 30), Later);

        Assert.Equal(ContractStatus.Closed, Assert.Single(employee.Contracts).Status);
        Assert.Equal(EmployeeStatus.Deactivated, employee.Status);
    }

    [Fact]
    public void DeactivatingWithoutAnActiveContractIsRejected()
    {
        var employee = Hire();
        Terminate(employee);

        Assert.Equal("hr.employee.not_active", Rejects(() => employee.Deactivate(new DateOnly(2026, 12, 1), Later)).Code);
    }

    [Fact]
    public void TerminatingWithAScheduledRecordAfterTheDateIsRejected()
    {
        var employee = Hire();
        employee.AddSalary(
            employee.ActiveContract!.Id, SalaryRecordId.New(), new SalaryEntry(new DateOnly(2026, 12, 1), 2_800_000m, false, false),
            Floor2026, null, Later);

        var exception = Rejects(() => Terminate(employee));

        Assert.Equal("hr.contract.records_after_end", exception.Code);
    }

    [Fact]
    public void TerminatingBeforeTheStartIsOutOfRange()
    {
        var employee = Hire();

        Assert.Throws<ArgumentOutOfRangeException>(() => Terminate(employee, "2026-01-14"));
    }

    [Fact]
    public void ReincorporatingCreatesANewContractAndKeepsHistory()
    {
        var employee = Hire();
        var first = employee.ActiveContract!;
        Terminate(employee);

        employee.Reincorporate(Contract(Terms(start: new DateOnly(2026, 12, 15), end: new DateOnly(2027, 6, 14))), Floor2026, Later);

        Assert.Equal(2, employee.Contracts.Count);
        Assert.Equal(EmployeeStatus.Active, employee.Status);
        Assert.NotSame(first, employee.ActiveContract);
        Assert.Equal(ContractStatus.Terminated, first.Status);
        Assert.Equal(new DateOnly(2026, 11, 30), first.EndedOn);
    }

    [Fact]
    public void ReincorporatingOnOrBeforeThePreviousEndIsRejected()
    {
        var employee = Hire();
        Terminate(employee);

        var exception = Rejects(() =>
            employee.Reincorporate(Contract(Terms(start: new DateOnly(2026, 11, 30), end: new DateOnly(2027, 6, 14))), Floor2026, Later));

        Assert.Equal("hr.contract.start_not_after_previous", exception.Code);
    }

    [Fact]
    public void ReincorporatingAnActiveEmployeeIsRejected()
    {
        var employee = Hire();

        var exception = Rejects(() =>
            employee.Reincorporate(Contract(Terms(start: new DateOnly(2026, 12, 15), end: new DateOnly(2027, 6, 14))), Floor2026, Later));

        Assert.Equal("hr.employee.already_active", exception.Code);
    }

    [Fact]
    public void ReincorporatingMayUseAnotherCompany()
    {
        var employee = Hire();
        var first = employee.ActiveContract!;
        Terminate(employee);

        employee.Reincorporate(
            Contract(Terms(start: new DateOnly(2026, 12, 15), end: new DateOnly(2027, 6, 14), companyId: OtherCompanyId)), Floor2026, Later);

        Assert.Equal(OtherCompanyId, employee.ActiveContract!.CompanyId);
        Assert.Equal(CompanyId, first.CompanyId);
    }

    [Fact]
    public void ANewHireCanBeDeleted()
    {
        var employee = Hire();

        Assert.True(employee.CanDelete);
        employee.EnsureDeletable();
    }

    private static Employee Mutated(string mutation)
    {
        var employee = Hire();
        var id = employee.ActiveContract!.Id;
        switch (mutation)
        {
            case "salary":
                employee.AddSalary(
                    id, SalaryRecordId.New(), new SalaryEntry(new DateOnly(2026, 12, 1), 2_800_000m, false, false), Floor2026, null, Later);
                break;
            case "assignment":
                employee.AddAssignment(
                    id,
                    AssignmentRecordId.New(),
                    new AssignmentEntry(new DateOnly(2026, 12, 15), ManagerId, null, Warehouse.SiteId, Warehouse.Id),
                    null,
                    Later);
                break;
            case "renewal":
                employee.Renew(id, ContractRenewalId.New(), new DateOnly(2027, 1, 14), null, Later);
                break;
            case "indefinite":
                employee.ConvertToIndefinite(id, Today, Later);
                break;
            case "terminate":
                Terminate(employee);
                break;
        }

        return employee;
    }

    [Theory]
    [InlineData("salary")]
    [InlineData("assignment")]
    [InlineData("renewal")]
    [InlineData("indefinite")]
    [InlineData("terminate")]
    public void AnyLaterRecordForbidsDeleting(string mutation)
    {
        var employee = Mutated(mutation);

        Assert.False(employee.CanDelete);
        Assert.Equal("hr.employee.not_deletable", Rejects(employee.EnsureDeletable).Code);
    }

    [Fact]
    public void EveryMutationBumpsTheVersion()
    {
        var employee = Hire();
        var id = employee.ActiveContract!.Id;
        var version = employee.Version;

        employee.UpdateProfile(Profile(), Later);
        Assert.Equal(++version, employee.Version);

        employee.SetPayment(new PaymentDetails(PaymentMethod.Other, null, null, null), Later);
        Assert.Equal(++version, employee.Version);
        Assert.Equal(PaymentMethod.Other, employee.Payment!.Method);

        employee.SetContractDocument(id, new DocumentRef(Guid.NewGuid(), "contrato.pdf"), Later);
        Assert.Equal(++version, employee.Version);

        employee.Renew(id, ContractRenewalId.New(), new DateOnly(2027, 1, 14), null, Later);
        Assert.Equal(++version, employee.Version);

        Terminate(employee);
        Assert.Equal(++version, employee.Version);
        Assert.Equal(Later, employee.UpdatedAt);
    }

    // El validador de la solicitud ya exige la fecha, pero el agregado no depende de él: `Renew` lee `EndDate` sin comprobarlo, así
    // que un término fijo o una obra sin fin no puede llegar a existir (revisión del bloque B).
    [Theory]
    [InlineData(ContractDuration.FixedTerm)]
    [InlineData(ContractDuration.WorkOrLabor)]
    public void AContractThatEndsNeedsAnEndDateWhereverItIsCreated(ContractDuration duration)
    {
        var withoutEnd = new ContractTerms(
            CompanyId, ContractType.Employee, duration, SalaryType.Monthly, new DateOnly(2026, 1, 15), null);
        var next = Contract(Terms(start: new DateOnly(2026, 12, 1), end: new DateOnly(2027, 6, 30))) with { Terms = withoutEnd with { StartDate = new DateOnly(2026, 12, 1) } };

        Assert.Throws<ArgumentException>(() => Hire(Contract(withoutEnd)));

        var converting = Hire();
        Assert.Throws<ArgumentException>(() => converting.ChangeContractType(converting.ActiveContract!.Id, next, Floor2026, Later));

        var retired = Hire();
        Terminate(retired);
        Assert.Throws<ArgumentException>(() => retired.Reincorporate(
            next with { Terms = withoutEnd with { StartDate = new DateOnly(2026, 12, 15) } }, Floor2026, Later));
    }

    [Fact]
    public void AnIndefiniteContractNeedsNoEndDate()
    {
        var employee = Hire(Contract(Terms(duration: ContractDuration.Indefinite)));

        Assert.Null(employee.ActiveContract!.EndDate);
    }

    [Fact]
    public void AnUnknownContractIsNotFound()
    {
        var employee = Hire();

        var exception = Rejects(() => employee.Renew(
            EmploymentContractId.New(), ContractRenewalId.New(), new DateOnly(2027, 1, 14), null, Later));

        Assert.Equal("hr.contract.not_found", exception.Code);
    }
}
