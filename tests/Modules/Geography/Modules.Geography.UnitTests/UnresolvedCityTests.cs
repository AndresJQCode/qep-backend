using Modules.Geography.Domain;

namespace Modules.Geography.UnitTests;

public sealed class UnresolvedCityTests
{
    private static readonly DateTimeOffset Seen = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

    private static UnresolvedCity NewPending() =>
        UnresolvedCity.Create(UnresolvedCityId.New(), DepartmentId.New(), " Calli  Valle ", "Calle 1 # 2-3", "VAC", Seen);

    [Fact]
    public void CreateStartsPendingWithNormalizedText()
    {
        var city = NewPending();

        Assert.Equal(UnresolvedCityStatus.Pending, city.Status);
        Assert.Equal("CALLI VALLE", city.NormalizedText);
        Assert.Equal("Calli  Valle", city.CityText);
        Assert.Equal("VAC", city.ProvinceCode);
        Assert.Null(city.SynonymId);
        Assert.Equal(Seen, city.FirstSeenAt);
        Assert.Equal(Seen, city.LastSeenAt);
        Assert.Equal(1, city.Version);
    }

    [Fact]
    public void ResolveDiscardRestoreFollowTheirStates()
    {
        var city = NewPending();
        var synonymId = CitySynonymId.New();
        var t1 = Seen.AddMinutes(1);
        var t2 = Seen.AddMinutes(2);
        var t3 = Seen.AddMinutes(3);

        city.Resolve(synonymId, t1);
        Assert.Equal(UnresolvedCityStatus.Resolved, city.Status);
        Assert.Equal(synonymId, city.SynonymId);
        Assert.Equal(2, city.Version);

        var other = NewPending();
        other.Discard(t2);
        Assert.Equal(UnresolvedCityStatus.Discarded, other.Status);
        Assert.Equal(2, other.Version);

        other.Restore(t3);
        Assert.Equal(UnresolvedCityStatus.Pending, other.Status);
        Assert.Equal(3, other.Version);
    }

    [Fact]
    public void ResolveOutsidePendingIs422NotPending()
    {
        var city = NewPending();
        city.Discard(Seen);

        var resolve = Assert.Throws<GeographyDomainException>(() => city.Resolve(CitySynonymId.New(), Seen));
        var discard = Assert.Throws<GeographyDomainException>(() => city.Discard(Seen));

        Assert.Equal("geography.unresolved.not_pending", resolve.Code);
        Assert.Equal("geography.unresolved.not_pending", discard.Code);
    }

    [Fact]
    public void RestoreOutsideDiscardedIs422NotDiscarded()
    {
        var city = NewPending();

        var ex = Assert.Throws<GeographyDomainException>(() => city.Restore(Seen));

        Assert.Equal("geography.unresolved.not_discarded", ex.Code);
    }

    [Fact]
    public void CreateTruncatesWithoutThrowing()
    {
        var city = UnresolvedCity.Create(
            UnresolvedCityId.New(), DepartmentId.New(), new string('x', 200), new string('y', 700), null, Seen);

        Assert.Equal(120, city.CityText.Length);
        Assert.Equal(120, city.NormalizedText.Length);
        Assert.Equal(600, city.SampleAddress.Length);
        Assert.Null(city.ProvinceCode);
    }
}
