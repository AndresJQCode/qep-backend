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
