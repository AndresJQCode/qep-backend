using Modules.Payroll.Domain;
using Modules.Payroll.Domain.Engine;

namespace Modules.Payroll.UnitTests.Engine;

public sealed class EmployeeDeductionsRuleTests
{
    private static readonly MonthAccumulators AfterTheFirstHalf = MonthAccumulators.Empty with
    {
        WorkedDays = 15,
        SalaryRaw = 875_452.5m,
        Salary = 875_453m,
        HealthEmployee = 35_018m,
        PensionEmployee = 35_018m,
    };

    private static IReadOnlyList<PayrollLine> Deductions(EngineInput input)
    {
        var chain = EngineTestData.Earnings(input);
        var ibc = IbcRule.Compute(input, chain.Worked, chain.Salary.Line.Amount, chain.Month);
        return EmployeeDeductionsRule.Compute(input, ibc);
    }

    private static PayrollLine FlatIbc(decimal ibc) =>
        new("ibc", PayrollLineGroup.Info, "Ingreso base de cotización del mes", ibc, null, new LineTrace("", [], [], null));

    [Fact]
    public void SecondHalfSubtractsTheMonthAdvance()
    {
        var prior = MonthAccumulators.Empty with
        {
            WorkedDays = 13,
            SalaryRaw = 1_300_000m,
            Salary = 1_300_000m,
            HealthEmployee = 60_000m,
            PensionEmployee = 60_000m,
        };
        var input = EngineTestData.Input(new DateOnly(2026, 12, 16), 3_000_000m) with
        {
            Prior = prior,
            Vacations =
            [
                EngineTestData.Vacation(
                    new DateOnly(2026, 12, 14), new DateOnly(2027, 1, 5), paidHere: false, advance: new VacationAdvance(60_000m, 60_000m, 0m)),
            ],
        };
        var chain = EngineTestData.Earnings(input);
        var ibc = IbcRule.Compute(input, chain.Worked, chain.Vacations, chain.Salary.Line.Amount, chain.Month);

        var lines = EmployeeDeductionsRule.Compute(input, ibc, chain.Vacations);

        Assert.Equal(3_000_000m, ibc.SocialSecurityIbc);
        Assert.Equal([0m, 0m], lines.Select(line => line.Amount));
        Assert.All(lines, line => Assert.Contains(line.Trace.Inputs, item => item.Name == "Anticipo de vacaciones del mes" && Equals(item.Value, 60_000m)));
    }

    // Review focus 3: an advance bigger than what the quincena owes must never become a refund of money the next one cannot withhold.
    [Fact]
    public void TheAdvanceNeverTurnsTheLineIntoARefund()
    {
        var input = EngineTestData.Input(new DateOnly(2026, 12, 16), 3_000_000m) with
        {
            Prior = MonthAccumulators.Empty with { HealthEmployee = 38_000m, PensionEmployee = 38_000m },
        };
        var ibc = new IbcOutcome(1_900_000m, 1_900_000m, 1_900_000m, FlatIbc(1_900_000m));
        var vacations = VacationDaysOutcome.None with { MonthAdvance = new VacationAdvance(70_036m, 70_036m, 0m) };

        var lines = EmployeeDeductionsRule.Compute(input, ibc, vacations);

        Assert.Equal([0m, 0m], lines.Select(line => line.Amount));
    }

    [Fact]
    public void WithoutAnAdvanceNothingChanges()
    {
        var input = EngineTestData.Input(new DateOnly(2026, 12, 16), 3_000_000m) with
        {
            Prior = MonthAccumulators.Empty with { HealthEmployee = 38_000m, PensionEmployee = 38_000m },
        };
        var ibc = new IbcOutcome(1_900_000m, 1_900_000m, 1_900_000m, FlatIbc(1_900_000m));

        var lines = EmployeeDeductionsRule.Compute(input, ibc, VacationDaysOutcome.None);

        Assert.Equal([38_000m, 38_000m], lines.Select(line => line.Amount));
        Assert.DoesNotContain(lines.SelectMany(line => line.Trace.Inputs), item => item.Name == "Anticipo de vacaciones del mes");
    }

    [Fact]
    public void TheFirstHalfWithholdsOnItsOwnIbc()
    {
        var lines = Deductions(EngineTestData.Input(new DateOnly(2026, 11, 1)));

        Assert.Equal(["health_employee", "pension_employee"], lines.Select(line => line.Code));
        Assert.All(lines, line => Assert.Equal(PayrollLineGroup.Deduction, line.Group));
        Assert.All(lines, line => Assert.Equal(35_018m, line.Amount));
    }

    [Fact]
    public void TheSecondHalfSettlesTheMonth()
    {
        var input = EngineTestData.Input(new DateOnly(2026, 11, 16)) with { Prior = AfterTheFirstHalf };

        var lines = Deductions(input);

        Assert.All(lines, line => Assert.Equal(35_018m, line.Amount));
    }

    [Fact]
    public void TheSettlementRoundsTheMonthNotTheHalf()
    {
        var input = EngineTestData.Input(new DateOnly(2026, 11, 16)) with { Prior = AfterTheFirstHalf };

        var health = Deductions(input)[0];

        // 1 750 905 x 0.04 = 70 036.2 -> 70 036 for the month, to the peso: the hundred is rounded on the PILA, not on the employee.
        Assert.Equal(70_036m, health.Amount + 35_018m);
        Assert.Contains(health.Trace.LegalValues, value => value.Key == "HealthEmployeeRate");
    }
}
