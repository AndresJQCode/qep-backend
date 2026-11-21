using Modules.Hr.Domain;

namespace Modules.Hr.UnitTests;

public sealed class LegalParametersTests
{
    [Fact]
    public void ResolvesTheValueInForceOnTheDate()
    {
        Assert.Equal(1_423_500m, LegalParameters.Resolve(HrTestData.LegalTable, LegalParameterKey.Smmlv, new DateOnly(2025, 12, 31)));
        Assert.Equal(1_750_905m, LegalParameters.Resolve(HrTestData.LegalTable, LegalParameterKey.Smmlv, new DateOnly(2026, 1, 1)));
    }

    [Fact]
    public void BeforeTheFirstValueThereIsNone() =>
        Assert.Null(LegalParameters.Resolve(HrTestData.LegalTable, LegalParameterKey.Smmlv, new DateOnly(2023, 12, 31)));

    // D-8: a year nobody loaded yet keeps the last value in force instead of failing.
    [Fact]
    public void ALaterYearKeepsTheLastLoaded() =>
        Assert.Equal(1_750_905m, LegalParameters.Resolve(HrTestData.LegalTable, LegalParameterKey.Smmlv, new DateOnly(2027, 6, 1)));
}
