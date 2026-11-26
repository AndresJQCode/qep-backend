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

    // El motor de nómina necesita saber desde cuándo rige el valor (la jornada cambia el 15 de julio).
    [Fact]
    public void ResolveEntryKeepsTheEffectiveDate()
    {
        var before = LegalParameters.ResolveEntry(HrTestData.LegalTable, LegalParameterKey.WeeklyHours, new DateOnly(2026, 7, 14));
        var after = LegalParameters.ResolveEntry(HrTestData.LegalTable, LegalParameterKey.WeeklyHours, new DateOnly(2026, 7, 15));

        Assert.Equal(new LegalParameterEntry(LegalParameterKey.WeeklyHours, new DateOnly(2025, 7, 15), 44m), before);
        Assert.Equal(new LegalParameterEntry(LegalParameterKey.WeeklyHours, new DateOnly(2026, 7, 15), 42m), after);
    }

    [Fact]
    public void BracketsOfTheYearInForce()
    {
        var brackets = LegalParameters.ResolveBrackets(HrTestData.BracketTable, LegalBracketKey.SolidarityFund, new DateOnly(2026, 3, 1));

        Assert.Equal(6, brackets.Count);
        Assert.All(brackets, bracket => Assert.Equal(new DateOnly(2026, 1, 1), bracket.EffectiveFrom));
        Assert.Equal([4m, 16m, 17m, 18m, 19m, 20m], brackets.Select(bracket => bracket.LowerBound));
    }
}
