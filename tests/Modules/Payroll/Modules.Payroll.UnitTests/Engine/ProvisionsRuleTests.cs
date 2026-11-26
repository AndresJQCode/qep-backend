using Modules.Payroll.Domain;
using Modules.Payroll.Domain.Engine;

namespace Modules.Payroll.UnitTests.Engine;

public sealed class ProvisionsRuleTests
{
    private static readonly EngineInput Input = EngineTestData.Input(new DateOnly(2026, 11, 1));

    private static PayrollLine Line(IReadOnlyList<PayrollLine> lines, string code) => lines.Single(line => line.Code == code);

    [Fact]
    public void SeveranceAndPremiumIncludeTransport()
    {
        var lines = ProvisionsRule.Compute(Input, 875_453m, 124_548m, 0m, 0m);

        Assert.Equal(["severance", "severance_interest", "premium", "vacation"], lines.Select(line => line.Code));
        Assert.All(lines, line => Assert.Equal(PayrollLineGroup.Provision, line.Group));
        Assert.Equal(83_300m, Line(lines, "severance").Amount);
        Assert.Equal(83_300m, Line(lines, "premium").Amount);
    }

    // Spec «Provisiones»: the days count as service time, the money paid in advance does not.
    [Fact]
    public void VacationDaysAreServiceTimeButThePayIsNot()
    {
        var lines = ProvisionsRule.Compute(Input, 0m, 0m, 0m, 0m, 1_500_000m);

        Assert.Equal(124_950m, Line(lines, "severance").Amount);
        Assert.Equal(15_000m, Line(lines, "severance_interest").Amount);
        Assert.Equal(124_950m, Line(lines, "premium").Amount);
        Assert.Equal(62_550m, Line(lines, "vacation").Amount);
        Assert.Contains(Line(lines, "severance").Trace.Inputs, item => item.Name == "Días de vacaciones como tiempo de servicio");
        Assert.DoesNotContain(
            ProvisionsRule.Compute(Input, 0m, 0m, 0m, 0m).SelectMany(line => line.Trace.Inputs),
            item => item.Name == "Días de vacaciones como tiempo de servicio");
    }

    [Fact]
    public void InterestIsOnePercentOfTheSeveranceBase()
    {
        var lines = ProvisionsRule.Compute(Input, 875_453m, 124_548m, 0m, 0m);

        Assert.Equal(10_000m, Line(lines, "severance_interest").Amount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100_000)]
    public void VacationExcludesTransportAndOvertime(int overtime)
    {
        var lines = ProvisionsRule.Compute(Input, 875_453m, 124_548m, overtime, 0m);

        Assert.Equal(36_506m, Line(lines, "vacation").Amount);
    }

    [Fact]
    public void SalaryIncomeCountsEverywhere()
    {
        var lines = ProvisionsRule.Compute(Input, 1_000_000m, 0m, 0m, 200_000m);

        Assert.Equal(99_960m, Line(lines, "severance").Amount);
        Assert.Equal(50_040m, Line(lines, "vacation").Amount);
    }
}
