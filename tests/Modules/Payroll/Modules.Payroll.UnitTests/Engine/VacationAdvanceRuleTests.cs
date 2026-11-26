using Modules.Payroll.Domain;
using Modules.Payroll.Domain.Engine;

namespace Modules.Payroll.UnitTests.Engine;

/// <summary>Spec «Álgebra del anticipo»: lo que la quincena que paga retiene por adelantado de los días de otras quincenas.</summary>
public sealed class VacationAdvanceRuleTests
{
    private static readonly DateOnly January = new(2027, 1, 1);

    private static EngineInput PayingInput(decimal previousIbc = 3_000_000m) =>
        EngineTestData.Input(January, 3_000_000m) with
        {
            Vacations =
            [
                EngineTestData.Vacation(new DateOnly(2027, 1, 12), new DateOnly(2027, 2, 1), paidHere: true, previousIbc: previousIbc),
            ],
        };

    private static decimal Amount(VacationAdvanceOutcome outcome, string code) =>
        outcome.Lines.Single(line => line.Code == code).Amount;

    [Fact]
    public void AdvancesEveryOtherMonth()
    {
        var input = PayingInput();

        var outcome = VacationAdvanceRule.Compute(input, 10_000_000m);

        Assert.Equal(["health_employee_vacation_advance", "pension_employee_vacation_advance"], outcome.Lines.Select(line => line.Code));
        Assert.All(outcome.Lines, line => Assert.Equal(PayrollLineGroup.Deduction, line.Group));
        Assert.Equal(["Anticipo de salud por vacaciones", "Anticipo de pensión por vacaciones"], outcome.Lines.Select(line => line.Label));
        Assert.Equal(64_000m, Amount(outcome, "health_employee_vacation_advance"));
        Assert.Equal(64_000m, Amount(outcome, "pension_employee_vacation_advance"));

        var photo = Assert.Single(outcome.Photos);
        var vacation = input.Vacations[0];
        Assert.Equal(vacation.VacationId, photo.VacationId);
        Assert.Equal(3_000_000m, photo.SalaryAtStart);
        Assert.Equal(vacation.SalaryRecordDate, photo.SalaryRecordDate);
        Assert.Equal(3_000_000m, photo.PreviousMonthIbc);
        Assert.Equal(VacationIbcSource.Typed, photo.PreviousIbcSource);
        Assert.Equal(
            [
                new VacationQuincenaDays(new DateOnly(2027, 1, 1), new DateOnly(2027, 1, 15), 4),
                new VacationQuincenaDays(new DateOnly(2027, 1, 16), new DateOnly(2027, 1, 31), 15),
                new VacationQuincenaDays(new DateOnly(2027, 2, 1), new DateOnly(2027, 2, 15), 1),
            ],
            photo.Quincenas);
        Assert.Equal(
            [
                new VacationMonthAdvance(new DateOnly(2027, 1, 1), 1_500_000m, 60_000m, 60_000m, 0m),
                new VacationMonthAdvance(new DateOnly(2027, 2, 1), 100_000m, 4_000m, 4_000m, 0m),
            ],
            photo.Months);
    }

    [Fact]
    public void RetainsUpToTheNet()
    {
        var outcome = VacationAdvanceRule.Compute(PayingInput(), 100_000m);

        Assert.Equal(60_000m, Amount(outcome, "health_employee_vacation_advance"));
        Assert.Equal(40_000m, Amount(outcome, "pension_employee_vacation_advance"));
        // The photo keeps what was withheld, not what was computed: the later quincena only subtracts that.
        Assert.Equal(
            [
                new VacationMonthAdvance(new DateOnly(2027, 1, 1), 1_500_000m, 60_000m, 40_000m, 0m),
                new VacationMonthAdvance(new DateOnly(2027, 2, 1), 100_000m, 0m, 0m, 0m),
            ],
            outcome.Photos[0].Months);
        Assert.All(outcome.Lines, line => Assert.Equal("Se retiene hasta dejar el neto en 0", line.Trace.Note));
    }

    [Fact]
    public void ANoteOnlyWhenSomethingWasLeftOut()
    {
        var outcome = VacationAdvanceRule.Compute(PayingInput(), 10_000_000m);

        Assert.All(outcome.Lines, line => Assert.Null(line.Trace.Note));
    }

    [Fact]
    public void ABigIbcAlsoAdvancesTheSolidarityFund()
    {
        var outcome = VacationAdvanceRule.Compute(PayingInput(previousIbc: 8_000_000m), 100_000_000m);

        Assert.Equal(
            [
                "health_employee_vacation_advance",
                "pension_employee_vacation_advance",
                "solidarity_fund_vacation_advance",
            ],
            outcome.Lines.Select(line => line.Code));
        Assert.Equal("Anticipo del FSP por vacaciones", outcome.Lines[2].Label);
        Assert.Equal(42_700m, Amount(outcome, "solidarity_fund_vacation_advance"));
        Assert.Equal([40_000m, 2_700m], outcome.Photos[0].Months.Select(month => month.SolidarityFund));
    }

    [Fact]
    public void ATypedIbcAboveTheCapIsAdvancedOnTheCap()
    {
        var cap = 25m * EngineTestData.Smmlv;
        var typed = VacationAdvanceRule.Compute(PayingInput(previousIbc: 100_000_000m), 1_000_000_000m);
        var capped = VacationAdvanceRule.Compute(PayingInput(previousIbc: cap), 1_000_000_000m);

        Assert.Equal(capped.Lines.Select(line => (line.Code, line.Amount)), typed.Lines.Select(line => (line.Code, line.Amount)));
        Assert.Equal(capped.Photos[0].Months, typed.Photos[0].Months);
        // What was typed is kept as typed: only the advance is computed on the cap.
        Assert.Equal(100_000_000m, typed.Photos[0].PreviousMonthIbc);
    }

    [Fact]
    public void AVacationInsideTheQuincenaAdvancesNothing()
    {
        var input = EngineTestData.Input(January, 3_000_000m) with
        {
            Vacations = [EngineTestData.Vacation(new DateOnly(2027, 1, 5), new DateOnly(2027, 1, 8), paidHere: true)],
        };

        var outcome = VacationAdvanceRule.Compute(input, 10_000_000m);

        Assert.Empty(outcome.Lines);
        Assert.Empty(Assert.Single(outcome.Photos).Months);
    }

    [Fact]
    public void OnlyTheVacationsThatPayHereGetAPhoto()
    {
        var input = EngineTestData.Input(January, 3_000_000m) with
        {
            Vacations = [EngineTestData.Vacation(new DateOnly(2026, 12, 14), new DateOnly(2027, 1, 5), paidHere: false)],
        };

        var outcome = VacationAdvanceRule.Compute(input, 10_000_000m);

        Assert.Empty(outcome.Lines);
        Assert.Empty(outcome.Photos);
    }

    [Fact]
    public void TwoVacationsOfTheSameQuincenaShareTheNetOfIt()
    {
        // One inside the quincena (advances nothing) and one that leaves it: both are photographed, and the net is spent once.
        var inside = EngineTestData.Vacation(new DateOnly(2027, 1, 4), new DateOnly(2027, 1, 5), paidHere: true);
        var leaving = EngineTestData.Vacation(new DateOnly(2027, 1, 12), new DateOnly(2027, 2, 1), paidHere: true);
        var input = EngineTestData.Input(January, 3_000_000m) with { Vacations = [inside, leaving] };

        var outcome = VacationAdvanceRule.Compute(input, 100_000m);

        Assert.Equal([inside.VacationId, leaving.VacationId], outcome.Photos.Select(photo => photo.VacationId));
        Assert.Empty(outcome.Photos[0].Months);
        Assert.Equal(60_000m, Amount(outcome, "health_employee_vacation_advance"));
        Assert.Equal(40_000m, Amount(outcome, "pension_employee_vacation_advance"));
        Assert.Equal(
            [new VacationMonthAdvance(new DateOnly(2027, 1, 1), 1_500_000m, 60_000m, 40_000m, 0m),
             new VacationMonthAdvance(new DateOnly(2027, 2, 1), 100_000m, 0m, 0m, 0m)],
            outcome.Photos[1].Months);
    }
}
