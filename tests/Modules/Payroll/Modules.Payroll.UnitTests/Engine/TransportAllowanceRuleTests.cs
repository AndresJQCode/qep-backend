using Modules.Payroll.Domain;
using Modules.Payroll.Domain.Engine;

namespace Modules.Payroll.UnitTests.Engine;

public sealed class TransportAllowanceRuleTests
{
    private static readonly DateOnly FirstHalf = new(2026, 11, 1);
    private static readonly DateOnly SecondHalf = new(2026, 11, 16);

    private static EngineInput WithCapBase(EngineInput input, TransportCapBase capBase) =>
        input with { Settings = input.Settings with { TransportCapBase = capBase } };

    private static EngineInput WithCommissions(EngineInput input, decimal amount) =>
        input with { Incomes = [EngineTestData.Income(SystemConceptKey.Commissions, amount, true)] };

    [Fact]
    public void BiweeklyPaysTheDays()
    {
        var transport = EngineTestData.Earnings(EngineTestData.Input(FirstHalf)).Transport;

        Assert.Equal("transport_allowance", transport.Line.Code);
        Assert.Equal(PayrollLineGroup.Earning, transport.Line.Group);
        Assert.Equal(124_548m, transport.Line.Amount);
        Assert.Equal(124_547.5m, transport.Raw);
    }

    [Fact]
    public void TheSecondHalfCompletesTheMonth()
    {
        var input = EngineTestData.Input(SecondHalf) with
        {
            Prior = MonthAccumulators.Empty with { WorkedDays = 15, TransportRaw = 124_547.5m, Transport = 124_548m },
        };

        var transport = EngineTestData.Earnings(input).Transport;

        Assert.Equal(124_547m, transport.Line.Amount);
    }

    [Fact]
    public void UpToTwiceTheMinimumKeepsIt()
    {
        var input = WithCommissions(WithCapBase(EngineTestData.Input(FirstHalf, 3_000_000m), TransportCapBase.SalaryAndSalaryIncome), 500_000m);

        Assert.Equal(124_548m, EngineTestData.Earnings(input).Transport.Line.Amount);
    }

    [Fact]
    public void OverTwiceTheMinimumLosesIt()
    {
        var input = WithCommissions(WithCapBase(EngineTestData.Input(FirstHalf, 3_000_000m), TransportCapBase.SalaryAndSalaryIncome), 501_811m);

        var transport = EngineTestData.Earnings(input).Transport;

        Assert.Equal(0m, transport.Line.Amount);
        Assert.Equal(0m, transport.Raw);
        Assert.Equal("El ingreso del mes supera 2 SMMLV", transport.Line.Trace.Note);
    }

    [Fact]
    public void ByDefaultOnlyTheSalaryCounts()
    {
        var input = WithCommissions(EngineTestData.Input(FirstHalf, 3_000_000m), 501_811m);

        Assert.Equal(124_548m, EngineTestData.Earnings(input).Transport.Line.Amount);
    }

    [Fact]
    public void PaidAbsencesBaseCountsOnlyTheSalaryUntilAbsencesExist()
    {
        // 3 400 000 + 200 000 of commissions: with salary-only (R-22) the cap income is 3 400 000 <= 3 501 810, so it is kept.
        var input = WithCommissions(WithCapBase(EngineTestData.Input(FirstHalf, 3_400_000m), TransportCapBase.SalaryAndPaidAbsences), 200_000m);
        var keeps = EngineTestData.Earnings(input);

        Assert.Equal(3_400_000m, keeps.Month.TransportCapIncome);
        Assert.Equal(124_548m, keeps.Transport.Line.Amount);

        // The same capture under salary-plus-income reaches 3 600 000 and loses it.
        var loses = EngineTestData.Earnings(WithCapBase(input, TransportCapBase.SalaryAndSalaryIncome));
        Assert.Equal(0m, loses.Transport.Line.Amount);
    }

    [Fact]
    public void TheCapNoteNamesTheLegalMultiple()
    {
        var input = WithCommissions(WithCapBase(EngineTestData.Input(FirstHalf, 3_000_000m), TransportCapBase.SalaryAndSalaryIncome), 501_811m);

        Assert.Equal("El ingreso del mes supera 2 SMMLV", EngineTestData.Earnings(input).Transport.Line.Trace.Note);
    }

    [Fact]
    public void MonthlyPaysNothingInTheFirstHalf()
    {
        var input = EngineTestData.Input(FirstHalf);
        input = input with { Settings = input.Settings with { TransportFrequency = TransportFrequency.Monthly } };

        var transport = EngineTestData.Earnings(input).Transport;

        Assert.Equal(0m, transport.Line.Amount);
        Assert.Equal("Se paga en la segunda quincena", transport.Line.Trace.Note);
    }

    [Fact]
    public void MonthlyPaysTheWholeMonthInTheSecond()
    {
        var first = FirstHalfOutcome(TransportFrequency.Monthly);

        Assert.Equal(249_095m, EngineTestData.Earnings(SecondHalfAfter(first, TransportFrequency.Monthly)).Transport.Line.Amount);
    }

    // Monthly keeps the line at 0 in the first half but still carries the half's raw amount (249 095 x 15 / 30 = 124 547.5) for the second.
    [Fact]
    public void MonthlyFirstHalfPaysNothingButCarriesTheRaw()
    {
        var first = FirstHalfOutcome(TransportFrequency.Monthly);

        Assert.Equal(0m, first.Line.Amount);
        Assert.Equal(124_547.5m, first.Raw);
    }

    // Switch A: Biweekly first half paid 124 548; Monthly second half pays round(124 547.5 + 124 547.5) - 124 548 = 249 095 - 124 548 = 124 547.
    [Fact]
    public void SwitchingFromBiweeklyToMonthlyDoesNotPayTwice()
    {
        var first = FirstHalfOutcome(TransportFrequency.Biweekly);

        var second = EngineTestData.Earnings(SecondHalfAfter(first, TransportFrequency.Monthly)).Transport;

        Assert.Equal(124_548m, first.Line.Amount);
        Assert.Equal(124_547m, second.Line.Amount);
    }

    // Switch B: Monthly first half paid 0 (raw 124 547.5); Biweekly second half pays round(124 547.5 + 124 547.5) - 0 = 249 095.
    [Fact]
    public void SwitchingFromMonthlyToBiweeklyDoesNotDropTheFirstHalf()
    {
        var first = FirstHalfOutcome(TransportFrequency.Monthly);

        var second = EngineTestData.Earnings(SecondHalfAfter(first, TransportFrequency.Biweekly)).Transport;

        Assert.Equal(249_095m, second.Line.Amount);
    }

    private static TransportOutcome FirstHalfOutcome(TransportFrequency frequency)
    {
        var input = EngineTestData.Input(FirstHalf);
        return EngineTestData.Earnings(input with { Settings = input.Settings with { TransportFrequency = frequency } }).Transport;
    }

    private static EngineInput SecondHalfAfter(TransportOutcome first, TransportFrequency frequency)
    {
        var input = EngineTestData.Input(SecondHalf) with
        {
            Prior = MonthAccumulators.Empty with { WorkedDays = 15, TransportRaw = first.Raw, Transport = first.Line.Amount },
        };
        return input with { Settings = input.Settings with { TransportFrequency = frequency } };
    }

    [Fact]
    public void ATeleworkerGetsConnectivityInstead()
    {
        var input = EngineTestData.Input(FirstHalf) with
        {
            Salaries = [new SalarySegmentSource(new DateOnly(2025, 7, 1), EngineTestData.Smmlv, Teleworker: true)],
        };

        var transport = EngineTestData.Earnings(input).Transport;

        Assert.Equal("connectivity_allowance", transport.Line.Code);
        Assert.Equal(124_548m, transport.Line.Amount);
    }

    [Fact]
    public void ATeleworkerRecordBeforeTheLawGetsTransport()
    {
        var input = EngineTestData.Input(FirstHalf) with
        {
            Salaries = [new SalarySegmentSource(new DateOnly(2025, 6, 24), EngineTestData.Smmlv, Teleworker: true)],
        };

        Assert.Equal("transport_allowance", EngineTestData.Earnings(input).Transport.Line.Code);
    }

    [Fact]
    public void TheTraceCitesTheLegalValuesItRead()
    {
        var line = EngineTestData.Earnings(EngineTestData.Input(FirstHalf)).Transport.Line;

        Assert.Contains(line.Trace.LegalValues, value => value.Key == "TransportAllowance" && value.Value == 249_095m);
        Assert.Contains(line.Trace.LegalValues, value => value.Key == "TransportAllowanceCapSmmlv");
        Assert.Contains(line.Trace.LegalValues, value => value.Key == "Smmlv");
    }
}
