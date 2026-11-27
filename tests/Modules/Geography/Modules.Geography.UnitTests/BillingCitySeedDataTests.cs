using Modules.Geography.Infrastructure.Seed;

namespace Modules.Geography.UnitTests;

public sealed class BillingCitySeedDataTests
{
    [Fact]
    public void TheEmbeddedListHasTheSiigoCitiesOutsideColombia()
    {
        using var stream = GeographySeeder.OpenResource(BillingCitySeeder.ResourceSuffix);

        var records = BillingCityDataParser.Parse(stream);

        Assert.Equal(3482, records.Count);
        Assert.Equal(219, records.Select(r => r.SiigoCountryCode.ToUpperInvariant()).Distinct().Count());
        Assert.DoesNotContain(records, r => r.SiigoCountryCode.Equals("CO", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            records,
            r => r is { SiigoCountryCode: "Us", StateCode: "09", CityCode: "1", CityName: "Miami", StateName: "Florida" });
    }

    [Fact]
    public void ARepeatedNameInACountryIsKeptBecauseTheListHasThem()
    {
        using var stream = GeographySeeder.OpenResource(BillingCitySeeder.ResourceSuffix);

        var records = BillingCityDataParser.Parse(stream);

        Assert.Equal(2, records.Count(r => r.SiigoCountryCode == "Us" && r.CityName == "Boston"));
    }

    [Fact]
    public void TheHashChangesWithTheSeederVersionAndWithTheBytes()
    {
        using var first = new MemoryStream([1, 2, 3]);
        using var same = new MemoryStream([1, 2, 3]);
        using var other = new MemoryStream([1, 2, 4]);

        var hash = BillingCitySeeder.ComputeHash(first);

        Assert.Equal(hash, BillingCitySeeder.ComputeHash(same));
        Assert.NotEqual(hash, BillingCitySeeder.ComputeHash(other));
        Assert.Matches("^[0-9a-f]{64}$", hash);
    }
}
