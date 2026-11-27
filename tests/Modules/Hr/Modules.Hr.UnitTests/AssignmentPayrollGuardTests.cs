using Modules.Hr.Application;
using Modules.Hr.Domain;
using static Modules.Hr.UnitTests.HrTestData;

namespace Modules.Hr.UnitTests;

/// <summary>Contrato del 2026-01-15 al 2026-07-14; registros 01-15 (<c>AnalystId</c>) y 04-01 (<c>ManagerId</c>).</summary>
public sealed class AssignmentPayrollGuardTests
{
    private static readonly DateTimeOffset Later = Now.AddMinutes(5);

    private static (Employee Employee, AssignmentRecord First, AssignmentRecord Second) Scene(NewContract? contract = null)
    {
        var employee = Hire(contract);
        var id = AssignmentRecordId.New();
        employee.AddAssignment(
            employee.ActiveContract!.Id,
            id,
            new AssignmentEntry(new DateOnly(2026, 4, 1), ManagerId, null, Warehouse.SiteId, Warehouse.Id),
            null,
            Later);
        var assignments = employee.ActiveContract.Assignments;
        return (employee, assignments.Single(record => record.Id != id), assignments.Single(record => record.Id == id));
    }

    private static FrozenPayrollPeriod Period(DateOnly start, DateOnly end, Guid? companyId = null) =>
        new(companyId ?? CompanyId, start, end);

    [Fact]
    public void ARecordRulesUntilTheDayBeforeTheNext()
    {
        var (employee, first, second) = Scene();
        var contract = employee.ActiveContract!;

        Assert.Equal((new DateOnly(2026, 1, 15), (DateOnly?)new DateOnly(2026, 3, 31)), AssignmentPayrollGuard.RangeOf(contract, first));
        Assert.Equal((new DateOnly(2026, 4, 1), (DateOnly?)new DateOnly(2026, 7, 14)), AssignmentPayrollGuard.RangeOf(contract, second));
    }

    [Fact]
    public void TheLastRecordOfAnOpenContractReachesEveryLaterPeriod()
    {
        var (employee, _, second) = Scene(Contract(Terms(duration: ContractDuration.Indefinite)));
        var contract = employee.ActiveContract!;

        var range = AssignmentPayrollGuard.RangeOf(contract, second);

        Assert.Null(range.To);
        Assert.True(AssignmentPayrollGuard.Reaches(
            [Period(new DateOnly(2030, 1, 1), new DateOnly(2030, 1, 15))], CompanyId, range.From, range.To));
    }

    [Fact]
    public void MovingADateBackReachesTheDaysThePreviousRecordRuled()
    {
        var (employee, _, second) = Scene();
        var contract = employee.ActiveContract!;
        var oldDate = second.EffectiveDate;
        employee.RewriteAssignment(
            contract.Id,
            second.Id,
            new AssignmentEntry(new DateOnly(2026, 3, 1), ManagerId, null, Warehouse.SiteId, Warehouse.Id),
            true,
            Later);

        var range = AssignmentPayrollGuard.RewriteRange(oldDate, contract, second);

        Assert.Equal((new DateOnly(2026, 3, 1), (DateOnly?)new DateOnly(2026, 7, 14)), range);
        var exception = Rejects(() => AssignmentPayrollGuard.EnsureFree(
            [Period(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 15))], CompanyId, range));
        Assert.Equal("hr.assignment.used_by_payroll", exception.Code);
    }

    [Fact]
    public void APeriodThatEndsTheDayBeforeDoesNotReach()
    {
        Assert.False(AssignmentPayrollGuard.Reaches(
            [Period(new DateOnly(2026, 3, 16), new DateOnly(2026, 3, 31))], CompanyId, new DateOnly(2026, 4, 1), new DateOnly(2026, 7, 14)));
    }

    [Fact]
    public void AFrozenPeriodOfAnotherCompanyDoesNotBlock()
    {
        Assert.False(AssignmentPayrollGuard.Reaches(
            [Period(new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 15), OtherCompanyId)],
            CompanyId,
            new DateOnly(2026, 4, 1),
            new DateOnly(2026, 7, 14)));
    }
}
