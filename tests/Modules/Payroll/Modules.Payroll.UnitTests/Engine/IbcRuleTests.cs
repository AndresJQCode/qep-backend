using Modules.Payroll.Domain;
using Modules.Payroll.Domain.Engine;

namespace Modules.Payroll.UnitTests.Engine;

public sealed class IbcRuleTests
{
    private static readonly DateOnly FirstHalf = new(2026, 11, 1);

    private static IbcOutcome Ibc(EngineInput input)
    {
        var chain = EngineTestData.Earnings(input);
        return IbcRule.Compute(input, chain.Worked, chain.Salary.Line.Amount, chain.Month);
    }

    private static IbcOutcome IbcWithVacations(EngineInput input)
    {
        var chain = EngineTestData.Earnings(input);
        return IbcRule.Compute(input, chain.Worked, chain.Vacations, chain.Salary.Line.Amount, chain.Month);
    }

    private static EngineInput WithVacation(DateOnly periodDay, MonthAccumulators prior, Func<VacationInput, VacationInput>? adjust = null)
    {
        var vacation = EngineTestData.Vacation(new DateOnly(2026, 12, 14), new DateOnly(2027, 1, 5), paidHere: false);
        return EngineTestData.Input(periodDay, 3_000_000m) with
        {
            Prior = prior,
            Vacations = [adjust?.Invoke(vacation) ?? vacation],
        };
    }

    [Fact]
    public void TheVacationDaysUseThePreviousIbc()
    {
        var outcome = IbcWithVacations(WithVacation(new DateOnly(2026, 12, 1), MonthAccumulators.Empty));

        Assert.Equal(1_500_000m, outcome.SocialSecurityIbc);
        Assert.Equal(1_300_000m, outcome.ArlBase);
        Assert.Equal(1_500_000m, outcome.ParafiscalBase);
        Assert.Equal(1_500_000m, outcome.Line.Amount);
        Assert.Contains(outcome.Line.Trace.Inputs, item => item.Name == "IBC anterior a las vacaciones" && Equals(item.Value, 3_000_000m));
        Assert.Contains(outcome.Line.Trace.Inputs, item => item.Name == "Días de vacaciones del mes" && Equals(item.Value, 2));
        Assert.Contains(outcome.Line.Trace.Inputs, item => item.Name == "Parte de vacaciones" && Equals(item.Value, 200_000m));
    }

    [Fact]
    public void TheSecondHalfCountsTheFirstHalfsVacationDays()
    {
        var prior = MonthAccumulators.Empty with { WorkedDays = 13, SalaryRaw = 1_300_000m, Salary = 1_300_000m };

        var outcome = IbcWithVacations(WithVacation(new DateOnly(2026, 12, 16), prior));

        Assert.Equal(3_000_000m, outcome.SocialSecurityIbc);
        Assert.Equal(1_300_000m, outcome.ArlBase);
        Assert.Equal(3_000_000m, outcome.ParafiscalBase);
    }

    [Fact]
    public void FallbackSalaryNote()
    {
        var outcome = IbcWithVacations(WithVacation(
            new DateOnly(2026, 12, 1),
            MonthAccumulators.Empty,
            vacation => vacation with { PreviousIbcSource = VacationIbcSource.Salary }));

        Assert.Equal("Sin IBC del mes anterior en QEP: se usa el salario", outcome.Line.Trace.Note);
    }

    [Fact]
    public void WithoutVacationsAllThreeBasesAgree()
    {
        var outcome = IbcWithVacations(EngineTestData.Input(new DateOnly(2026, 11, 1)));

        Assert.Equal(outcome.SocialSecurityIbc, outcome.ArlBase);
        Assert.Equal(outcome.SocialSecurityIbc, outcome.ParafiscalBase);
        Assert.DoesNotContain(outcome.Line.Trace.Inputs, item => item.Name == "Parte de vacaciones");
    }

    [Fact]
    public void WithoutNonSalaryTheIbcIsTheSalaryEarnings()
    {
        var input = EngineTestData.Input(new DateOnly(2026, 8, 1), 2_100_000m) with
        {
            Overtime = [EngineTestData.Overtime(SystemConceptKey.ExtraDay, 10m)],
            Incomes = [EngineTestData.Income(SystemConceptKey.Commissions, 100_000m, true)],
        };

        var outcome = Ibc(input);

        Assert.Equal(1_275_000m, outcome.ParafiscalBase);
        Assert.Equal(1_275_000m, outcome.SocialSecurityIbc);
        Assert.Equal("ibc", outcome.Line.Code);
        Assert.Equal(PayrollLineGroup.Info, outcome.Line.Group);
        Assert.Equal(1_275_000m, outcome.Line.Amount);
    }

    [Fact]
    public void NonSalaryOverFortyPercentAddsTheExcess()
    {
        var input = EngineTestData.Input(FirstHalf, 2_000_000m) with
        {
            Incomes = [EngineTestData.Income(SystemConceptKey.OtherIncome, 1_000_000m, false)],
        };

        var outcome = Ibc(input);

        Assert.Equal(1_200_000m, outcome.SocialSecurityIbc);
        Assert.Equal(1_000_000m, outcome.ParafiscalBase);
    }

    [Fact]
    public void ExactlyFortyPercentAddsNothing()
    {
        var input = EngineTestData.Input(FirstHalf, 2_400_000m) with
        {
            Incomes = [EngineTestData.Income(SystemConceptKey.OtherIncome, 800_000m, false)],
        };

        Assert.Equal(1_200_000m, Ibc(input).SocialSecurityIbc);
    }

    [Fact]
    public void TheFloorLiftsAnIbcBelowTheMinimumByDays()
    {
        // Eligibility would exclude a 1 200 000 salary: the floor is a defensive guard, so the input is built by hand.
        var input = EngineTestData.Input(FirstHalf, 1_200_000m);

        var outcome = Ibc(input);

        Assert.Equal(600_000m, outcome.ParafiscalBase);
        Assert.Equal(875_453m, outcome.SocialSecurityIbc);
    }

    [Fact]
    public void TheCapIsTwentyFiveMinimums()
    {
        var input = EngineTestData.Input(new DateOnly(2026, 11, 16), 50_000_000m) with
        {
            Prior = MonthAccumulators.Empty with { WorkedDays = 15, SalaryRaw = 25_000_000m, Salary = 25_000_000m },
        };

        Assert.Equal(43_772_625m, Ibc(input).SocialSecurityIbc);
    }

    [Fact]
    public void TheIbcRoundsUpToThePeso()
    {
        var input = EngineTestData.Input(FirstHalf, 2_000_000m) with
        {
            Incomes = [EngineTestData.Income(SystemConceptKey.OtherIncome, 1_000_001m, false)],
        };

        // total 2 000 001 x 0.6 = 1 200 000.6
        Assert.Equal(1_200_001m, Ibc(input).SocialSecurityIbc);
    }

    [Fact]
    public void TransportIsOutsideTheIbc()
    {
        var input = EngineTestData.Input(FirstHalf);
        var chain = EngineTestData.Earnings(input);

        var outcome = Ibc(input);

        Assert.Equal(124_548m, chain.Transport.Line.Amount);
        Assert.Equal(875_453m, outcome.SocialSecurityIbc);
    }

    [Fact]
    public void TheTraceCitesTheLegalValues()
    {
        var line = Ibc(EngineTestData.Input(FirstHalf)).Line;

        Assert.Contains(line.Trace.LegalValues, value => value.Key == "NonSalaryCapRate");
        Assert.Contains(line.Trace.LegalValues, value => value.Key == "IbcCapSmmlv");
        Assert.Contains(line.Trace.LegalValues, value => value.Key == "Smmlv");
    }
}
