using Modules.Hr.Domain;

namespace Modules.Hr.UnitTests;

public sealed class LegalParameterYearsTests
{
    [Fact]
    public void MissingWhenNoJanuaryFirst() =>
        Assert.True(LegalParameterYears.MissingFor([new DateOnly(2026, 1, 1), new DateOnly(2027, 3, 1)], 2027));

    [Fact]
    public void NotMissingWithAJanuaryFirst() =>
        Assert.False(LegalParameterYears.MissingFor([new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 1)], 2027));

    [Fact]
    public void MissingWithoutAnyDate() =>
        Assert.True(LegalParameterYears.MissingFor([], 2026));

    [Theory]
    [InlineData(2026, 11, 30, false)]
    [InlineData(2026, 12, 1, true)]
    [InlineData(2026, 12, 31, true)]
    [InlineData(2027, 1, 1, false)]
    public void WatchOpensOnDecemberFirst(int year, int month, int day, bool open) =>
        Assert.Equal(open, LegalParameterYears.NextYearWatchOpen(new DateOnly(year, month, day)));
}
