using Modules.Payroll.Domain;
using Modules.Payroll.Domain.Engine;

namespace Modules.Payroll.UnitTests.Engine;

public sealed class SolidarityFundRuleTests
{
    private static PayrollLine? Fund(EngineInput input)
    {
        var chain = EngineTestData.Earnings(input);
        var ibc = IbcRule.Compute(input, chain.Worked, chain.Salary.Line.Amount, chain.Month);
        return SolidarityFundRule.Compute(input, chain.Salary.SalaryInForce, ibc);
    }

    private static PayrollLine? FundWithAdvance(EngineInput input, decimal advance)
    {
        var chain = EngineTestData.Earnings(input);
        var ibc = IbcRule.Compute(input, chain.Worked, chain.Vacations, chain.Salary.Line.Amount, chain.Month);
        var vacations = VacationDaysOutcome.None with { MonthAdvance = new VacationAdvance(0m, 0m, advance) };
        return SolidarityFundRule.Compute(input, chain.Salary.SalaryInForce, ibc, vacations);
    }

    private static EngineInput SecondHalfOfAHighSalary(decimal salary, decimal withheldBefore) =>
        EngineTestData.Input(new DateOnly(2026, 11, 16), salary) with
        {
            Prior = MonthAccumulators.Empty with
            {
                WorkedDays = 15,
                SalaryRaw = salary / 2,
                Salary = salary / 2,
                SolidarityFund = withheldBefore,
            },
        };

    [Fact]
    public void TheMonthAdvanceIsSubtracted()
    {
        var line = FundWithAdvance(SecondHalfOfAHighSalary(7_500_000m, 37_500m), 37_500m);

        Assert.NotNull(line);
        Assert.Equal(0m, line.Amount);
        Assert.Contains(line.Trace.Inputs, item => item.Name == "Anticipo de vacaciones del mes" && Equals(item.Value, 37_500m));
    }

    [Fact]
    public void TheAdvanceNeverTurnsTheLineIntoARefund()
    {
        var line = FundWithAdvance(SecondHalfOfAHighSalary(7_500_000m, 37_500m), 100_000m);

        Assert.NotNull(line);
        Assert.Equal(0m, line.Amount);
    }

    [Fact]
    public void AnAdvanceKeepsTheLineEvenWithoutABracket()
    {
        var line = FundWithAdvance(EngineTestData.Input(new DateOnly(2026, 11, 16)), 1_000m);

        Assert.NotNull(line);
        Assert.Equal(0m, line.Amount);
    }

    [Fact]
    public void BelowFourMinimumsThereIsNone() =>
        Assert.Null(Fund(EngineTestData.Input(new DateOnly(2026, 11, 1))));

    [Fact]
    public void TheFirstHalfDecidesBySalaryAlone()
    {
        var line = Fund(EngineTestData.Input(new DateOnly(2026, 11, 1), 7_500_000m));

        Assert.NotNull(line);
        Assert.Equal("solidarity_fund", line.Code);
        Assert.Equal(PayrollLineGroup.Deduction, line.Group);
        Assert.Equal(37_500m, line.Amount);
    }

    [Fact]
    public void TheSecondHalfRefundsWhenTheMonthFallsBelow()
    {
        var input = EngineTestData.Input(new DateOnly(2026, 11, 16), 7_500_000m) with
        {
            ContractEndedOn = new DateOnly(2026, 11, 20),
            Prior = MonthAccumulators.Empty with
            {
                WorkedDays = 15,
                SalaryRaw = 3_750_000m,
                Salary = 3_750_000m,
                SolidarityFund = 37_500m,
            },
        };

        var line = Fund(input);

        Assert.NotNull(line);
        Assert.Equal(-37_500m, line.Amount);
        Assert.Equal("Devolución de lo retenido en la primera quincena", line.Trace.Note);
    }

    [Fact]
    public void TheSecondHalfWithoutBracketNorPriorHasNoLine()
    {
        var input = EngineTestData.Input(new DateOnly(2026, 11, 16)) with
        {
            Prior = MonthAccumulators.Empty with { WorkedDays = 15, SalaryRaw = 875_452.5m, Salary = 875_453m },
        };

        Assert.Null(Fund(input));
    }

    [Fact]
    public void TheFirstHalfOfATopEarnerUsesTheTopBracket()
    {
        // 50 000 000 is 28.56 SMMLV: the bracket is looked up with the salary capped at 25 SMMLV, so it is the 2 % one (B-1).
        var line = Fund(EngineTestData.Input(new DateOnly(2026, 11, 1), 50_000_000m));

        Assert.NotNull(line);
        Assert.Equal(500_000m, line.Amount);
    }

    [Fact]
    public void TheTopBracketAppliesToTheCappedIbc()
    {
        var input = EngineTestData.Input(new DateOnly(2026, 11, 16), 50_000_000m) with
        {
            Prior = MonthAccumulators.Empty with
            {
                WorkedDays = 15,
                SalaryRaw = 25_000_000m,
                Salary = 25_000_000m,
                SolidarityFund = 500_000m,
            },
        };

        var line = Fund(input);

        Assert.NotNull(line);
        Assert.Equal(375_500m, line.Amount);
    }

    [Fact]
    public void ExactlyFourMinimumsPaysOnePercent()
    {
        var line = Fund(EngineTestData.Input(new DateOnly(2026, 11, 1), 4m * EngineTestData.Smmlv));

        Assert.NotNull(line);
        Assert.Equal(35_100m, line.Amount);
    }
}
