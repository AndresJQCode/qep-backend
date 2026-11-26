using Modules.Payroll.Domain;
using Modules.Payroll.Domain.Engine;

namespace Modules.Payroll.UnitTests.Engine;

public sealed class OvertimeRuleTests
{
    private static EngineInput August(decimal salary, params OvertimeInput[] overtime) =>
        EngineTestData.Input(new DateOnly(2026, 8, 1), salary) with { Overtime = overtime };

    [Fact]
    public void TheHourValueIsNotRounded()
    {
        var input = August(1_750_905m, EngineTestData.Overtime(SystemConceptKey.ExtraDay, 10m));

        var outcome = OvertimeRule.Compute(input, 1_750_905m, 0m);

        Assert.Equal(1_750_905m / 30m / 7m, outcome.HourValue, 6);
        Assert.NotEqual(Math.Round(outcome.HourValue), outcome.HourValue);
        Assert.Equal(104_221m, Assert.Single(outcome.Lines).Amount);
    }

    [Fact]
    public void HourValueUsesTheWeeklyHoursOfThePeriodStart()
    {
        var outcome = OvertimeRule.Compute(August(2_100_000m), 2_100_000m, 0m);

        Assert.Equal(10_000m, outcome.HourValue);
    }

    [Fact]
    public void ExtraDayPaysOnePointTwentyFive()
    {
        var input = August(2_100_000m, EngineTestData.Overtime(SystemConceptKey.ExtraDay, 10m));

        var outcome = OvertimeRule.Compute(input, 2_100_000m, 0m);

        var line = Assert.Single(outcome.Lines);
        Assert.Equal(125_000m, line.Amount);
        Assert.Equal(PayrollLineGroup.Earning, line.Group);
        Assert.Equal($"overtime:{SystemConcepts.All[0].Id}", line.Code);
        Assert.Equal("Hora extra diurna", line.Label);
        Assert.Equal(125_000m, outcome.Total);
        Assert.Null(line.SalaryConstitutive);
    }

    [Fact]
    public void TheSundaySurchargeIsOnlyTheSurcharge()
    {
        var input = August(2_100_000m, EngineTestData.Overtime(SystemConceptKey.SundaySurcharge, 8m));

        var outcome = OvertimeRule.Compute(input, 2_100_000m, 0m);

        Assert.Equal(72_000m, Assert.Single(outcome.Lines).Amount);
    }

    [Fact]
    public void TheJulyFirstHalfStillUsesFortyFourHours()
    {
        var input = EngineTestData.Input(new DateOnly(2026, 7, 1), 2_100_000m);

        var outcome = OvertimeRule.Compute(input, 2_100_000m, 0m);

        Assert.Equal(2_100_000m / 30m / (44m / 6m), outcome.HourValue, 6);
    }

    [Fact]
    public void TheMonthSalaryIncomeBaseAddsSalaryIncomes()
    {
        var input = August(2_100_000m) with
        {
            Settings = EngineTestData.Input(new DateOnly(2026, 8, 1)).Settings with { OvertimeBase = OvertimeBase.SalaryAndMonthSalaryIncome },
        };

        var outcome = OvertimeRule.Compute(input, 2_100_000m, 300_000m);

        Assert.Equal(2_400_000m / 30m / 7m, outcome.HourValue, 6);
    }

    [Fact]
    public void TheDefaultBaseIgnoresTheSalaryIncome()
    {
        var outcome = OvertimeRule.Compute(August(2_100_000m), 2_100_000m, 300_000m);

        Assert.Equal(10_000m, outcome.HourValue);
    }

    [Fact]
    public void ACustomConceptUsesItsMultiplier()
    {
        var custom = new OvertimeInput(Guid.NewGuid(), "Hora de turno", 10m, null, 1.4m);

        var outcome = OvertimeRule.Compute(August(2_100_000m, custom), 2_100_000m, 0m);

        var line = Assert.Single(outcome.Lines);
        Assert.Equal(140_000m, line.Amount);
        Assert.Equal($"overtime:{custom.ConceptId}", line.Code);
        Assert.Equal("Hora de turno", line.Label);
    }

    [Theory]
    [InlineData(2026, 6, 1, 2_200_000, 205_000)]
    [InlineData(2026, 8, 1, 2_100_000, 215_000)]
    public void ExtraSundayDayFollowsTheRamp(int year, int month, int day, int salary, int expected)
    {
        var input = EngineTestData.Input(new DateOnly(year, month, day), salary) with
        {
            Overtime = [EngineTestData.Overtime(SystemConceptKey.ExtraSundayDay, 10m)],
        };

        var outcome = OvertimeRule.Compute(input, salary, 0m);

        Assert.Equal(10_000m, outcome.HourValue);
        Assert.Equal((decimal)expected, Assert.Single(outcome.Lines).Amount);
    }

    [Fact]
    public void TheTraceListsTheFactorAndItsLegalValues()
    {
        var input = August(2_100_000m, EngineTestData.Overtime(SystemConceptKey.ExtraSundayDay, 10m));

        var line = Assert.Single(OvertimeRule.Compute(input, 2_100_000m, 0m).Lines);

        Assert.Contains(line.Trace.LegalValues, value => value.Key == "SundaySurchargeRate" && value.Value == 0.90m);
        Assert.Contains(line.Trace.LegalValues, value => value.Key == "WeeklyHours");
        Assert.Contains(line.Trace.Inputs, item => item.Name == "Horas");
    }
}
