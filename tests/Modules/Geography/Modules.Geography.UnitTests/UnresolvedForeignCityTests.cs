using Modules.Geography.Domain;

namespace Modules.Geography.UnitTests;

public sealed class UnresolvedForeignCityTests
{
    private static readonly DateTimeOffset Seen = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);

    private static UnresolvedForeignCity NewPending() =>
        UnresolvedForeignCity.Create(UnresolvedForeignCityId.New(), " us ", "  Miámy ", Seen);

    [Fact]
    public void CreateUppercasesTheCountryAndKeysTheText()
    {
        var row = NewPending();

        Assert.Equal(
            ("US", "MIAMY", "Miámy", UnresolvedCityStatus.Pending, 1L),
            (row.CountryCode, row.NormalizedText, row.CityText, row.Status, row.Version));
        Assert.Equal(row.NormalizedText, UnresolvedForeignCity.KeyFor("  Miámy "));
        Assert.Null(row.SynonymId);
        Assert.Equal((Seen, Seen, Seen), (row.FirstSeenAt, row.LastSeenAt, row.UpdatedAt));
    }

    [Fact]
    public void CreateTruncatesWithoutThrowing()
    {
        var row = UnresolvedForeignCity.Create(UnresolvedForeignCityId.New(), "US", new string('x', 200), Seen);

        Assert.Equal(120, row.CityText.Length);
        Assert.Equal(120, row.NormalizedText.Length);
        Assert.Equal(row.NormalizedText, UnresolvedForeignCity.KeyFor(new string('x', 200)));
    }

    [Fact]
    public void AMarkOnlyTextHasAnEmptyKey() =>
        Assert.Equal(string.Empty, UnresolvedForeignCity.KeyFor("́"));

    [Fact]
    public void ResolveDiscardRestoreFollowTheirStates()
    {
        var row = NewPending();
        var synonymId = ForeignCitySynonymId.New();

        row.Resolve(synonymId, Seen.AddMinutes(1));
        Assert.Equal((UnresolvedCityStatus.Resolved, synonymId, 2L), (row.Status, row.SynonymId, row.Version));

        var other = NewPending();
        other.Discard(Seen.AddMinutes(2));
        Assert.Equal(UnresolvedCityStatus.Discarded, other.Status);
        other.Restore(Seen.AddMinutes(3));
        Assert.Equal((UnresolvedCityStatus.Pending, 3L, Seen.AddMinutes(3)), (other.Status, other.Version, other.UpdatedAt));
    }

    [Fact]
    public void ResolvingTwiceIsNotPending()
    {
        var row = NewPending();
        row.Resolve(ForeignCitySynonymId.New(), Seen);

        var error = Assert.Throws<GeographyDomainException>(() => row.Resolve(ForeignCitySynonymId.New(), Seen));

        Assert.Equal("geography.unresolved.not_pending", error.Code);
    }

    [Fact]
    public void RestoringAPendingRowIsNotDiscarded()
    {
        var error = Assert.Throws<GeographyDomainException>(() => NewPending().Restore(Seen));

        Assert.Equal("geography.unresolved.not_discarded", error.Code);
    }
}
