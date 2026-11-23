using Modules.Payroll.Domain;
using Modules.Payroll.Domain.Engine;

namespace Modules.Payroll.UnitTests.Engine;

public sealed class EmployerContributionsRuleTests
{
    private static IReadOnlyList<PayrollLine> Contributions(EngineInput input)
    {
        var chain = EngineTestData.Earnings(input);
        var ibc = IbcRule.Compute(input, chain.Worked, chain.Salary.Line.Amount, chain.Month);
        return EmployerContributionsRule.Compute(input, ibc, chain.Month);
    }

    private static PayrollLine Line(IReadOnlyList<PayrollLine> lines, string code) => lines.Single(line => line.Code == code);

    [Fact]
    public void AnExoneratedCompanyBelowTenMinimumsPaysNoHealthIcbfOrSena()
    {
        var lines = Contributions(EngineTestData.Input(new DateOnly(2026, 11, 1)));

        Assert.Equal(
            ["health_employer", "pension_employer", "arl", "compensation_fund", "icbf", "sena"],
            lines.Select(line => line.Code));
        Assert.All(lines, line => Assert.Equal(PayrollLineGroup.EmployerContribution, line.Group));
        foreach (var code in new[] { "health_employer", "icbf", "sena" })
        {
            Assert.Equal(0m, Line(lines, code).Amount);
            Assert.Equal("Exonerado (ET art. 114-1)", Line(lines, code).Trace.Note);
        }

        Assert.Equal(35_100m, Line(lines, "compensation_fund").Amount);
        Assert.Equal(105_100m, Line(lines, "pension_employer").Amount);
        Assert.Equal(4_600m, Line(lines, "arl").Amount);
    }

    [Fact]
    public void TenMinimumsOrMoreAreNotExonerated()
    {
        var lines = Contributions(EngineTestData.Input(new DateOnly(2026, 11, 1), 17_509_050m));

        Assert.True(Line(lines, "health_employer").Amount > 0m);
        Assert.Null(Line(lines, "health_employer").Trace.Note);
    }

    [Fact]
    public void ANonExoneratedCompanyAlwaysPays()
    {
        var input = EngineTestData.Input(new DateOnly(2026, 11, 1));
        input = input with { Settings = input.Settings with { Exonerated = false } };

        var lines = Contributions(input);

        Assert.Equal(74_500m, Line(lines, "health_employer").Amount);
        Assert.Equal(26_300m, Line(lines, "icbf").Amount);
        Assert.Equal(17_600m, Line(lines, "sena").Amount);
    }

    [Fact]
    public void HighRiskPensionIsTheTotalMinusTheEmployeeShare()
    {
        var input = EngineTestData.Input(new DateOnly(2026, 11, 1)) with
        {
            Workplace = new WorkplaceInfo("Mina", ArlClass.I, HighRiskPension: true),
        };

        var line = Line(Contributions(input), "pension_employer");

        // 875 453 x 0.22 = 192 599.66 -> 192 600
        Assert.Equal(192_600m, line.Amount);
        Assert.Contains(line.Trace.Inputs, item => item.Name == "Tasa" && Equals(item.Value, 0.22m));
        Assert.Contains(line.Trace.LegalValues, value => value.Key == "HighRiskPensionTotalRate");
    }

    [Fact]
    public void ArlFollowsTheWorkplaceClass()
    {
        var input = EngineTestData.Input(new DateOnly(2026, 11, 1)) with
        {
            Workplace = new WorkplaceInfo("Obra", ArlClass.V, HighRiskPension: false),
        };

        var line = Line(Contributions(input), "arl");

        Assert.Equal(61_000m, line.Amount);
        Assert.Contains(line.Trace.Inputs, item => item.Name == "Tasa" && Equals(item.Value, 0.0696m));
    }

    [Fact]
    public void ArlFollowsTheIbcWhileParafiscalsIgnoreTheFortyPercentRule()
    {
        var input = EngineTestData.Input(new DateOnly(2026, 11, 1), 2_000_000m) with
        {
            Incomes = [EngineTestData.Income(SystemConceptKey.OtherIncome, 1_000_000m, false)],
        };

        var lines = Contributions(input);

        // ARL is social security: 1 200 000 (IBC with the 40 % excess) x 0.00522 = 6 264 -> 6 300. The fund stays on 1 000 000.
        Assert.Equal(6_300m, Line(lines, "arl").Amount);
        Assert.Contains(Line(lines, "arl").Trace.Inputs, item => item.Name == "IBC del mes" && Equals(item.Value, 1_200_000m));
        Assert.Equal(40_000m, Line(lines, "compensation_fund").Amount);
        Assert.Equal(0m, Line(lines, "health_employer").Amount);
        Assert.Equal(144_000m, Line(lines, "pension_employer").Amount);
    }

    [Fact]
    public void TheArlOfATopEarnerIsCappedLikeTheIbc()
    {
        var input = EngineTestData.Input(new DateOnly(2026, 11, 16), 50_000_000m) with
        {
            Prior = MonthAccumulators.Empty with
            {
                WorkedDays = 15, SalaryRaw = 25_000_000m, Salary = 25_000_000m, Arl = 130_500m, CompensationFund = 1_000_000m,
            },
        };

        var lines = Contributions(input);

        // IBC capped at 25 x 1 750 905 = 43 772 625; x 0.00522 = 228 493.10 -> 228 500 - 130 500.
        Assert.Equal(98_000m, Line(lines, "arl").Amount);
        // The fund has no cap: 50 000 000 x 0.04 = 2 000 000 - 1 000 000.
        Assert.Equal(1_000_000m, Line(lines, "compensation_fund").Amount);
    }

    [Fact]
    public void TheSecondHalfSettlesEachContribution()
    {
        var input = EngineTestData.Input(new DateOnly(2026, 11, 16)) with
        {
            Prior = MonthAccumulators.Empty with
            {
                WorkedDays = 15,
                SalaryRaw = 875_452.5m,
                Salary = 875_453m,
                PensionEmployer = 105_100m,
                Arl = 4_600m,
                CompensationFund = 35_100m,
            },
        };

        var lines = Contributions(input);

        Assert.Equal(105_100m, Line(lines, "pension_employer").Amount);
        Assert.Equal(4_600m, Line(lines, "arl").Amount);
        Assert.Equal(35_000m, Line(lines, "compensation_fund").Amount);
    }
}
