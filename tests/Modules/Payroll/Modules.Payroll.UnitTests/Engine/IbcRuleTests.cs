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
