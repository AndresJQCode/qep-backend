using Modules.Hr.Domain;

namespace Modules.Hr.UnitTests;

public sealed class LegalParameterUsageRuleTests
{
    private static readonly Guid CompanyA = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    private static readonly Guid CompanyB = Guid.Parse("b0000000-0000-0000-0000-000000000002");

    private static CalculatedPeriodStamp Stamp(int year, int month, int day, Guid company) =>
        new(new DateOnly(year, month, day), company);

    [Fact]
    public void NoStampInRangeIsNull()
    {
        var stamps = new[] { Stamp(2025, 6, 1, CompanyA) };

        Assert.Null(LegalParameterUsageRule.Affected(new DateOnly(2026, 1, 1), [], stamps));
    }

    [Fact]
    public void FirstAndSecondFortnightBothCount()
    {
        var stamps = new[] { Stamp(2026, 1, 1, CompanyA), Stamp(2026, 1, 16, CompanyA) };

        var usage = LegalParameterUsageRule.Affected(new DateOnly(2026, 1, 1), [], stamps);

        Assert.Equal(new LegalParameterUsageSummary(2, 1, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 16)), usage);
    }

    [Fact]
    public void TheNextRowClosesTheRange()
    {
        var stamps = new[] { Stamp(2026, 1, 16, CompanyA) };

        Assert.Null(LegalParameterUsageRule.Affected(new DateOnly(2025, 1, 1), [new DateOnly(2026, 1, 1)], stamps));
    }

    [Fact]
    public void ARetroactiveRowBetweenRowsCatchesTheSixteenth()
    {
        var stamps = new[] { Stamp(2026, 1, 16, CompanyA) };

        var usage = LegalParameterUsageRule.Affected(new DateOnly(2026, 1, 10), [new DateOnly(2026, 1, 1)], stamps);

        Assert.Equal(new LegalParameterUsageSummary(1, 1, new DateOnly(2026, 1, 16), new DateOnly(2026, 1, 16)), usage);
    }

    [Fact]
    public void ARetroactiveRowBeforeTheNextRowIsBoundedByIt()
    {
        var stamps = new[] { Stamp(2026, 1, 16, CompanyA), Stamp(2026, 3, 1, CompanyA) };

        var usage = LegalParameterUsageRule.Affected(
            new DateOnly(2026, 1, 10), [new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 1)], stamps);

        Assert.Equal(1, usage!.PeriodsAffected);
    }

    [Fact]
    public void AFutureRowWithoutPeriodsIsFree()
    {
        var stamps = new[] { Stamp(2026, 12, 16, CompanyA) };

        Assert.Null(LegalParameterUsageRule.Affected(new DateOnly(2027, 1, 1), [new DateOnly(2026, 1, 1)], stamps));
    }

    [Fact]
    public void CompaniesAreCountedDistinct()
    {
        var stamps = new[]
        {
            Stamp(2026, 1, 1, CompanyA), Stamp(2026, 1, 16, CompanyA), Stamp(2026, 1, 1, CompanyB),
        };

        var usage = LegalParameterUsageRule.Affected(new DateOnly(2026, 1, 1), [], stamps);

        Assert.Equal(3, usage!.PeriodsAffected);
        Assert.Equal(2, usage.CompaniesAffected);
    }
}
