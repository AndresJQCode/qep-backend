using Modules.Payroll.Domain;
using Modules.Payroll.Domain.Engine;

namespace Modules.Payroll.UnitTests.Engine;

public sealed class IncomeAndRecurringRuleTests
{
    private static RecurringOutcome Recurring(EngineInput input, int workedDays = 15)
    {
        var worked = new WorkedDays(input.Period.Start, input.Period.End, workedDays, false);
        return RecurringConceptRule.Compute(input, worked);
    }

    [Fact]
    public void SalaryAndNonSalaryIncomesAreSeparated()
    {
        var commissions = EngineTestData.Income(SystemConceptKey.Commissions, 500_000.40m, true);
        var other = EngineTestData.Income(SystemConceptKey.OtherIncome, 300_000m, false);
        var input = EngineTestData.Input(new DateOnly(2026, 11, 1)) with { Incomes = [commissions, other] };

        var outcome = IncomeRule.Compute(input);

        Assert.Equal(500_000m, outcome.SalaryIncome);
        Assert.Equal(300_000m, outcome.NonSalaryIncome);
        Assert.Equal(
            [$"income:{commissions.ConceptId}:0", $"income:{other.ConceptId}:1"],
            outcome.Lines.Select(line => line.Code));
        Assert.All(outcome.Lines, line => Assert.Equal(PayrollLineGroup.Earning, line.Group));
        Assert.Equal([true, false], outcome.Lines.Select(line => line.SalaryConstitutive));
        Assert.Equal("Comisiones", outcome.Lines[0].Label);
    }

    [Fact]
    public void AProportionalRecurringFollowsTheDays()
    {
        var recurringId = Guid.NewGuid();
        var input = EngineTestData.Input(new DateOnly(2026, 11, 1)) with
        {
            Recurring = [new RecurringInput(recurringId, "Bono", ConceptKind.Income, RecurringMode.Proportional, 100_000m, true, null)],
        };

        var outcome = Recurring(input, workedDays: 6);

        var line = Assert.Single(outcome.IncomeLines);
        Assert.Equal($"recurring:{recurringId}", line.Code);
        Assert.Equal(40_000m, line.Amount);
        Assert.Equal(40_000m, outcome.SalaryIncome);
        Assert.Equal(0m, outcome.NonSalaryIncome);
    }

    [Fact]
    public void ACapLimitsTheLastInstallment()
    {
        var input = EngineTestData.Input(new DateOnly(2026, 11, 1)) with
        {
            Recurring = [new RecurringInput(Guid.NewGuid(), "Préstamo", ConceptKind.Deduction, RecurringMode.Fixed, 300_000m, false, 120_000m)],
        };

        var outcome = Recurring(input);

        var line = Assert.Single(outcome.DeductionLines);
        Assert.Equal(120_000m, line.Amount);
        Assert.Equal(PayrollLineGroup.Deduction, line.Group);
        Assert.Null(line.SalaryConstitutive);
        Assert.Equal(120_000m, outcome.Deductions);
        Assert.Empty(outcome.IncomeLines);
    }

    [Fact]
    public void AnExhaustedRecurringHasNoLine()
    {
        var input = EngineTestData.Input(new DateOnly(2026, 11, 1)) with
        {
            Recurring = [new RecurringInput(Guid.NewGuid(), "Préstamo", ConceptKind.Deduction, RecurringMode.Fixed, 300_000m, false, 0m)],
        };

        var outcome = Recurring(input);

        Assert.Empty(outcome.DeductionLines);
        Assert.Empty(outcome.IncomeLines);
        Assert.Equal(0m, outcome.Deductions);
    }

    [Fact]
    public void ARecurringThatRoundsToZeroHasNoLine()
    {
        var input = EngineTestData.Input(new DateOnly(2026, 11, 1)) with
        {
            Recurring = [new RecurringInput(Guid.NewGuid(), "Bono", ConceptKind.Income, RecurringMode.Proportional, 1m, false, null)],
        };

        Assert.Empty(Recurring(input, workedDays: 1).IncomeLines);
    }

    [Fact]
    public void ANonSalaryRecurringIncomeGoesToTheNonSalaryTotal()
    {
        var input = EngineTestData.Input(new DateOnly(2026, 11, 1)) with
        {
            Recurring = [new RecurringInput(Guid.NewGuid(), "Auxilio", ConceptKind.Income, RecurringMode.Fixed, 80_000m, false, null)],
        };

        var outcome = Recurring(input);

        Assert.Equal(80_000m, outcome.NonSalaryIncome);
        Assert.Equal(0m, outcome.SalaryIncome);
    }
}
