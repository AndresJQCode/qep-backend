using Modules.Payroll.Domain;
using Modules.Payroll.Domain.Engine;

namespace Modules.Payroll.UnitTests.Engine;

public sealed class VacationDaysRuleTests
{
    private static VacationDaysOutcome Compute(EngineInput input)
    {
        var worked = WorkedDaysRule.Compute(input.Period, input.ContractStart, input.ContractEndedOn);
        return VacationDaysRule.Compute(input, worked);
    }

    [Fact]
    public void CountsTheDaysOfThisQuincenaAndOfTheMonth()
    {
        var input = EngineTestData.Input(new DateOnly(2026, 12, 16), 3_000_000m) with
        {
            Vacations = [EngineTestData.Vacation(new DateOnly(2026, 12, 14), new DateOnly(2027, 1, 5), paidHere: false)],
        };

        var outcome = Compute(input);

        Assert.Equal(15, outcome.Days);
        Assert.Equal(17, outcome.MonthDays);
        Assert.Equal(1_700_000m, outcome.IbcPart);
        Assert.Equal(1_700_000m, outcome.ParafiscalValue);
        Assert.Equal(1_500_000m, outcome.ServiceValue);
        Assert.Equal([new DateRange(new DateOnly(2026, 12, 16), new DateOnly(2026, 12, 31))], outcome.Ranges);
    }

    [Fact]
    public void ExternalUsesWhatSiigoPaid()
    {
        var input = EngineTestData.Input(new DateOnly(2026, 12, 1), 3_000_000m) with
        {
            Vacations =
            [
                EngineTestData.Vacation(
                    new DateOnly(2026, 11, 23), new DateOnly(2026, 12, 4), paidHere: false,
                    origin: VacationOrigin.External, monthPay: 480_000m),
            ],
        };

        var outcome = Compute(input);

        Assert.Equal(4, outcome.Days);
        Assert.Equal(4, outcome.MonthDays);
        Assert.Equal(480_000m, outcome.ParafiscalValue);
        Assert.Equal(480_000m, outcome.ServiceValue);
    }

    [Fact]
    public void WithoutVacationsThereIsNothing()
    {
        var outcome = Compute(EngineTestData.Input(new DateOnly(2026, 12, 1)));

        Assert.Equal(VacationDaysOutcome.None, outcome);
        Assert.Empty(outcome.Ranges);
    }

    [Fact]
    public void TheRangeIsCutAtTheDaysTheContractCovers()
    {
        // Only the range that cuts a salary segment is limited to the covered days: the counted days are what the planner trims.
        var input = EngineTestData.Input(new DateOnly(2026, 12, 16), 3_000_000m) with
        {
            ContractEndedOn = new DateOnly(2026, 12, 20),
            Vacations = [EngineTestData.Vacation(new DateOnly(2026, 12, 14), new DateOnly(2026, 12, 20), paidHere: false)],
        };

        var outcome = Compute(input);

        Assert.Equal([new DateRange(new DateOnly(2026, 12, 16), new DateOnly(2026, 12, 20))], outcome.Ranges);
        Assert.Equal(5, outcome.Days);
    }

    [Fact]
    public void TheRangeStartsAtTheContractStart()
    {
        var input = EngineTestData.Input(new DateOnly(2026, 12, 1), 3_000_000m) with
        {
            ContractStart = new DateOnly(2026, 12, 10),
            Vacations = [EngineTestData.Vacation(new DateOnly(2026, 12, 5), new DateOnly(2026, 12, 14), paidHere: false)],
        };

        var outcome = Compute(input);

        Assert.Equal([new DateRange(new DateOnly(2026, 12, 10), new DateOnly(2026, 12, 14))], outcome.Ranges);
    }
}
