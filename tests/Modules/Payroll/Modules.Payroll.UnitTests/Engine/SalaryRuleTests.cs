using Modules.Payroll.Domain;
using Modules.Payroll.Domain.Engine;

namespace Modules.Payroll.UnitTests.Engine;

public sealed class SalaryRuleTests
{
    private static SalaryOutcome Compute(EngineInput input)
    {
        var worked = WorkedDaysRule.Compute(input.Period, input.ContractStart, input.ContractEndedOn);
        return SalaryRule.Compute(input, worked);
    }

    private static SalaryOutcome ComputeWithVacations(EngineInput input)
    {
        var worked = WorkedDaysRule.Compute(input.Period, input.ContractStart, input.ContractEndedOn);
        return SalaryRule.Compute(input, worked, VacationDaysRule.Compute(input, worked));
    }

    private static EngineInput WithVacation(DateOnly periodDay, DateOnly start, DateOnly end) =>
        EngineTestData.Input(periodDay, 3_000_000m) with
        {
            Vacations = [EngineTestData.Vacation(start, end, paidHere: false)],
        };

    [Fact]
    public void PaysOnlyTheWorkedDays()
    {
        var outcome = ComputeWithVacations(WithVacation(new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 14), new DateOnly(2027, 1, 5)));

        Assert.Equal("salary", outcome.Line.Code);
        Assert.Equal(1_300_000m, outcome.Line.Amount);
        Assert.Contains(outcome.Line.Trace.Inputs, item => item is { Name: "Días trabajados", Value: 13 });
        Assert.Contains(outcome.Line.Trace.Inputs, item => item is { Name: "Días de vacaciones", Value: 2 });
    }

    [Fact]
    public void ZeroWorkedDaysPaysZero()
    {
        var outcome = ComputeWithVacations(WithVacation(new DateOnly(2026, 12, 16), new DateOnly(2026, 12, 14), new DateOnly(2027, 1, 5)));

        Assert.Equal(0m, outcome.Line.Amount);
        Assert.Equal(0m, outcome.Raw);
        Assert.Empty(outcome.Segments);
        Assert.Equal(3_000_000m, outcome.SalaryInForce);
    }

    [Fact]
    public void SegmentsAreCutOnWorkedDaysOnly()
    {
        var input = WithVacation(new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 14), new DateOnly(2027, 1, 5)) with
        {
            Salaries =
            [
                new SalarySegmentSource(new DateOnly(2025, 1, 1), 3_000_000m, false),
                new SalarySegmentSource(new DateOnly(2026, 12, 10), 3_300_000m, false),
            ],
        };

        var outcome = ComputeWithVacations(input);

        Assert.Equal([9, 4], outcome.Segments.Select(segment => segment.Days));
        Assert.Equal([3_000_000m, 3_300_000m], outcome.Segments.Select(segment => segment.MonthlyAmount));
        Assert.Equal(new DateOnly(2026, 12, 13), outcome.Segments[^1].To);
        Assert.Equal(1_340_000m, outcome.Line.Amount);
        Assert.Equal(3_300_000m, outcome.SalaryInForce);
    }

    [Fact]
    public void HalfOfTheMinimumWage()
    {
        var outcome = Compute(EngineTestData.Input(new DateOnly(2026, 11, 1)));

        Assert.Equal("salary", outcome.Line.Code);
        Assert.Equal(PayrollLineGroup.Earning, outcome.Line.Group);
        Assert.Equal(875_453m, outcome.Line.Amount);
        Assert.Equal(875_452.5m, outcome.Raw);
        Assert.Equal(EngineTestData.Smmlv, outcome.SalaryInForce);
    }

    [Fact]
    public void TheMonthAddsUpToTheMonthlySalary()
    {
        var first = Compute(EngineTestData.Input(new DateOnly(2026, 11, 1)));
        var secondInput = EngineTestData.Input(new DateOnly(2026, 11, 16)) with
        {
            Prior = MonthAccumulators.Empty with { WorkedDays = 15, SalaryRaw = first.Raw, Salary = first.Line.Amount },
        };

        var second = Compute(secondInput);

        Assert.Equal(875_452m, second.Line.Amount);
        Assert.Equal(1_750_905m, first.Line.Amount + second.Line.Amount);
    }

    [Fact]
    public void ARaiseMidPeriodSplitsIntoSegments()
    {
        var input = EngineTestData.Input(new DateOnly(2026, 11, 1)) with
        {
            Salaries =
            [
                new SalarySegmentSource(new DateOnly(2025, 1, 1), 1_750_905m, false),
                new SalarySegmentSource(new DateOnly(2026, 11, 10), 2_000_000m, false),
            ],
        };

        var outcome = Compute(input);

        Assert.Collection(
            outcome.Segments,
            first =>
            {
                Assert.Equal(new DateOnly(2026, 11, 1), first.From);
                Assert.Equal(new DateOnly(2026, 11, 9), first.To);
                Assert.Equal(9, first.Days);
                Assert.Equal(1_750_905m, first.MonthlyAmount);
            },
            last =>
            {
                Assert.Equal(new DateOnly(2026, 11, 10), last.From);
                Assert.Equal(new DateOnly(2026, 11, 15), last.To);
                Assert.Equal(6, last.Days);
                Assert.Equal(2_000_000m, last.MonthlyAmount);
            });
        Assert.Equal(1_750_905m / 30m * 9 + 2_000_000m / 30m * 6, outcome.Raw, 6);
        Assert.Equal(925_272m, outcome.Line.Amount);
        Assert.Equal(2_000_000m, outcome.SalaryInForce);
        Assert.Equal(new DateOnly(2026, 11, 10), outcome.SalaryRecordDate);
    }

    [Fact]
    public void TheLastSegmentAbsorbsTheDifference()
    {
        var input = EngineTestData.Input(new DateOnly(2026, 2, 16)) with
        {
            Salaries =
            [
                new SalarySegmentSource(new DateOnly(2025, 1, 1), 1_750_905m, false),
                new SalarySegmentSource(new DateOnly(2026, 2, 20), 2_000_000m, false),
            ],
        };

        var outcome = Compute(input);

        Assert.Equal([4, 11], outcome.Segments.Select(segment => segment.Days));
    }

    [Fact]
    public void TheTraceListsTheSegments()
    {
        var input = EngineTestData.Input(new DateOnly(2026, 11, 1)) with
        {
            Salaries =
            [
                new SalarySegmentSource(new DateOnly(2025, 1, 1), 1_750_905m, false),
                new SalarySegmentSource(new DateOnly(2026, 11, 10), 2_000_000m, false),
            ],
        };

        var outcome = Compute(input);

        Assert.False(string.IsNullOrWhiteSpace(outcome.Line.Trace.Formula));
        Assert.Equal(2, outcome.Line.Trace.Inputs.Count(item => item.Name.StartsWith("Tramo", StringComparison.Ordinal)));
        Assert.Empty(outcome.Line.Trace.LegalValues);
    }

    [Fact]
    public void ALaterRecordDoesNotLeakIntoThePeriod()
    {
        var input = EngineTestData.Input(new DateOnly(2026, 11, 1)) with
        {
            Salaries =
            [
                new SalarySegmentSource(new DateOnly(2025, 1, 1), 1_750_905m, false),
                new SalarySegmentSource(new DateOnly(2026, 12, 1), 3_000_000m, false),
            ],
        };

        var outcome = Compute(input);

        Assert.Single(outcome.Segments);
        Assert.Equal(1_750_905m, outcome.SalaryInForce);
    }
}
