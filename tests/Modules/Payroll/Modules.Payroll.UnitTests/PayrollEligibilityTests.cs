using Modules.Payroll.Application;
using Modules.Payroll.Domain;
using Modules.Payroll.Domain.Engine;

namespace Modules.Payroll.UnitTests;

public sealed class PayrollEligibilityTests
{
    private const decimal Smmlv = 1_750_905m;
    private static readonly DateOnly Start = new(2026, 11, 1);
    private static readonly DateOnly End = new(2026, 11, 15);

    private static HrAssignmentSource Assignment(DateOnly from, ArlClass? riskClass = ArlClass.III) =>
        new(from, "Analista", "Principal", "Bodega", riskClass, false, null);

    private static HrContractSource Contract(
        string contractType = "Employee",
        string salaryType = "Monthly",
        DateOnly? startDate = null,
        DateOnly? endedOn = null,
        IReadOnlyList<HrSalarySource>? salaries = null,
        IReadOnlyList<HrAssignmentSource>? assignments = null)
    {
        var begin = startDate ?? new DateOnly(2026, 1, 15);
        return new HrContractSource(
            Guid.NewGuid(),
            contractType,
            salaryType,
            begin,
            endedOn,
            salaries ?? [new HrSalarySource(begin, 2_500_000m, false)],
            assignments ?? [Assignment(begin)]);
    }

    private static HrEmployeeSource Employee(
        string subtype = "Standard", bool foreignExempt = false, params HrContractSource[] contracts) =>
        new(
            Guid.NewGuid(),
            "Ana",
            "Pérez",
            "CC",
            "1020304050",
            subtype,
            foreignExempt,
            contracts.Length == 0 ? [Contract()] : contracts,
            [DayOfWeek.Sunday]);

    private static EligibilityResult Evaluate(HrEmployeeSource employee) =>
        PayrollEligibility.Evaluate(employee, Start, End, Smmlv);

    [Theory]
    [InlineData("Contractor", "Monthly", "Standard", false, ExclusionReason.ContractTypeNotSupported)]
    [InlineData("Employee", "Integral", "Standard", false, ExclusionReason.SalaryTypeNotSupported)]
    [InlineData("Employee", "HalfTime", "Standard", false, ExclusionReason.SalaryTypeNotSupported)]
    [InlineData("Employee", "Monthly", "Pensioner", false, ExclusionReason.ContributorSubtypeNotSupported)]
    [InlineData("Employee", "Monthly", "Standard", true, ExclusionReason.ForeignPensionExempt)]
    public void UnsupportedEmployeesAreExcludedWithTheirReason(
        string contractType, string salaryType, string subtype, bool foreignExempt, ExclusionReason expected)
    {
        var result = Evaluate(Employee(subtype, foreignExempt, Contract(contractType, salaryType)));

        Assert.Equal(EligibilityStatus.Excluded, result.Status);
        Assert.Equal(expected, result.Exclusion);
        Assert.Empty(result.Blockers);
    }

    [Fact]
    public void ASalaryBelowTheMinimumIsExcluded()
    {
        var begin = new DateOnly(2026, 1, 15);
        var result = Evaluate(Employee(contracts: Contract(salaries: [new HrSalarySource(begin, 1_500_000m, false)])));

        Assert.Equal(EligibilityStatus.Excluded, result.Status);
        Assert.Equal(ExclusionReason.SalaryBelowMinimum, result.Exclusion);
    }

    [Fact]
    public void TwoContractsInThePeriodAreExcluded()
    {
        var first = Contract(startDate: new DateOnly(2026, 1, 15), endedOn: new DateOnly(2026, 11, 5));
        var second = Contract(startDate: new DateOnly(2026, 11, 10));

        var result = Evaluate(Employee(contracts: [first, second]));

        Assert.Equal(EligibilityStatus.Excluded, result.Status);
        Assert.Equal(ExclusionReason.MultipleContractsInPeriod, result.Exclusion);
        Assert.Null(result.Contract);
    }

    [Fact]
    public void ATypeExclusionWinsOverBlockers()
    {
        var result = Evaluate(Employee(contracts: Contract(contractType: "Contractor", assignments: [])));

        Assert.Equal(EligibilityStatus.Excluded, result.Status);
        Assert.Equal(ExclusionReason.ContractTypeNotSupported, result.Exclusion);
        Assert.Empty(result.Blockers);
    }

    [Fact]
    public void ABelowMinimumExclusionWinsOverBlockers()
    {
        var begin = new DateOnly(2026, 1, 15);
        var result = Evaluate(Employee(contracts: Contract(
            salaries: [new HrSalarySource(begin, 1_500_000m, false)], assignments: [])));

        Assert.Equal(EligibilityStatus.Excluded, result.Status);
        Assert.Equal(ExclusionReason.SalaryBelowMinimum, result.Exclusion);
        Assert.Empty(result.Blockers);
    }

    [Fact]
    public void AMissingWorkplaceBlocks()
    {
        var result = Evaluate(Employee(contracts: Contract(assignments: [])));

        Assert.Equal(EligibilityStatus.Blocked, result.Status);
        Assert.Null(result.Exclusion);
        Assert.Equal([BlockerReason.MissingWorkplace], result.Blockers);
    }

    // I-12: Hr exige la clase de riesgo al crear el centro, así que por la API no se puede producir; el chequeo es defensivo.
    [Fact]
    public void AWorkplaceWithoutRiskClassBlocks()
    {
        var begin = new DateOnly(2026, 1, 15);
        var result = Evaluate(Employee(contracts: Contract(assignments: [Assignment(begin, riskClass: null)])));

        Assert.Equal(EligibilityStatus.Blocked, result.Status);
        Assert.Equal([BlockerReason.MissingRiskClass], result.Blockers);
    }

    [Fact]
    public void BlockersAccumulate()
    {
        // El único salario y el único cargo empiezan después del último día cubierto: no hay ni salario ni lugar que lo cubra.
        var result = Evaluate(Employee(contracts: Contract(
            startDate: new DateOnly(2026, 1, 15),
            salaries: [new HrSalarySource(new DateOnly(2026, 11, 20), 2_500_000m, false)],
            assignments: [Assignment(new DateOnly(2026, 11, 20))])));

        Assert.Equal(EligibilityStatus.Blocked, result.Status);
        Assert.Equal([BlockerReason.MissingSalary, BlockerReason.MissingWorkplace], result.Blockers);
    }

    [Fact]
    public void AStandardMonthlyEmployeeIsLiquidable()
    {
        var begin = new DateOnly(2026, 1, 15);
        var contract = Contract(
            salaries: [new HrSalarySource(begin, 2_500_000m, false), new HrSalarySource(new DateOnly(2026, 11, 8), 2_800_000m, true)],
            assignments: [Assignment(begin), Assignment(new DateOnly(2026, 11, 8), ArlClass.IV)]);

        var result = Evaluate(Employee(contracts: contract));

        Assert.Equal(EligibilityStatus.Liquidable, result.Status);
        Assert.Null(result.Exclusion);
        Assert.Empty(result.Blockers);
        Assert.Same(contract, result.Contract);
        Assert.Equal(2_800_000m, result.SalaryAtEnd!.Amount);
        Assert.Equal(ArlClass.IV, result.AssignmentAtEnd!.RiskClass);
        Assert.Equal(End, result.LastCoveredDay);
    }

    [Fact]
    public void TheLastCoveredDayIsTheEndDateWhenTheContractEndsInside()
    {
        var begin = new DateOnly(2026, 1, 15);
        var contract = Contract(
            endedOn: new DateOnly(2026, 11, 9),
            salaries: [new HrSalarySource(begin, 2_500_000m, false), new HrSalarySource(new DateOnly(2026, 11, 12), 2_800_000m, false)]);

        var result = Evaluate(Employee(contracts: contract));

        Assert.Equal(EligibilityStatus.Liquidable, result.Status);
        Assert.Equal(new DateOnly(2026, 11, 9), result.LastCoveredDay);
        Assert.Equal(2_500_000m, result.SalaryAtEnd!.Amount);
    }

    private static Vacation VacationOn(DateOnly start, DateOnly end) => Vacation.Create(
        VacationId.New(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), VacationOrigin.Qep, start, end, 5, end.AddDays(1), null, [],
        new DateTimeOffset(2026, 11, 23, 15, 0, 0, TimeSpan.Zero));

    [Fact]
    public void AVacationAfterTheContractEndBlocks()
    {
        var contract = Contract(endedOn: new DateOnly(2026, 11, 12));
        var result = Evaluate(Employee(contracts: contract));

        var blocked = PayrollEligibility.WithVacations(result, [VacationOn(new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 20))]);

        Assert.Equal(EligibilityStatus.Blocked, blocked.Status);
        Assert.Equal([BlockerReason.VacationOutsideContract], blocked.Blockers);
        Assert.Same(contract, blocked.Contract);
    }

    [Fact]
    public void AVacationBeforeTheContractStartBlocks()
    {
        var result = Evaluate(Employee(contracts: Contract(startDate: new DateOnly(2026, 11, 8))));

        var blocked = PayrollEligibility.WithVacations(result, [VacationOn(new DateOnly(2026, 11, 3), new DateOnly(2026, 11, 10))]);

        Assert.Equal([BlockerReason.VacationOutsideContract], blocked.Blockers);
    }

    [Fact]
    public void AVacationInsideTheContractChangesNothing()
    {
        var result = Evaluate(Employee(contracts: Contract(endedOn: new DateOnly(2026, 11, 12))));

        var same = PayrollEligibility.WithVacations(result, [VacationOn(new DateOnly(2026, 11, 3), new DateOnly(2026, 11, 12))]);

        Assert.Equal(result, same);
        Assert.Equal(result, PayrollEligibility.WithVacations(result, []));
    }

    [Fact]
    public void AVacationOfAnOlderContractDoesNotBlockTheNewOne()
    {
        // The old contract ended on the 9th and its vacation does not touch the new contract at all: the builder drops it.
        var result = Evaluate(Employee(contracts: Contract(startDate: new DateOnly(2026, 11, 10))));

        var same = PayrollEligibility.WithVacations(result, [VacationOn(new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 6))]);

        Assert.Equal(result, same);
    }

    [Fact]
    public void AVacationAfterTheEndOfTheContractDoesNotBlock()
    {
        var result = Evaluate(Employee(contracts: Contract(endedOn: new DateOnly(2026, 11, 12))));

        var same = PayrollEligibility.WithVacations(result, [VacationOn(new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 25))]);

        Assert.Equal(result, same);
    }

    [Fact]
    public void TheVacationBlockerJoinsTheOthers()
    {
        var result = Evaluate(Employee(contracts: Contract(endedOn: new DateOnly(2026, 11, 12), assignments: [])));

        var blocked = PayrollEligibility.WithVacations(result, [VacationOn(new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 20))]);

        Assert.Equal([BlockerReason.MissingWorkplace, BlockerReason.VacationOutsideContract], blocked.Blockers);
    }

    [Fact]
    public void AnExcludedEmployeeIsReturnedAsIs()
    {
        var result = Evaluate(Employee(subtype: "Apprentice"));

        var same = PayrollEligibility.WithVacations(result, [VacationOn(new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 20))]);

        Assert.Same(result, same);
        Assert.Equal(EligibilityStatus.Excluded, same.Status);
    }
}
