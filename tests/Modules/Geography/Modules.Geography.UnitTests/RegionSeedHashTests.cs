using System.Text;
using Modules.Geography.Infrastructure.Seed;

namespace Modules.Geography.UnitTests;

public sealed class RegionSeedHashTests
{
    [Fact]
    public void TheSameInputGivesTheSameHash()
    {
        Assert.Equal(Hash("ES", "[1]", "[2]"), Hash("ES", "[1]", "[2]"));
    }

    [Theory]
    [InlineData("MX", "[1]", "[2]")]
    [InlineData("ES", "[1,1]", "[2]")]
    [InlineData("ES", "[1]", "[2,2]")]
    public void ADifferentCountryOrFileGivesADifferentHash(string country, string regions, string cities)
    {
        Assert.NotEqual(Hash("ES", "[1]", "[2]"), Hash(country, regions, cities));
    }

    // Mover bytes de un archivo al otro no puede dar el mismo hash.
    [Fact]
    public void TheBoundaryBetweenTheTwoFilesMatters()
    {
        Assert.NotEqual(Hash("ES", "ab", "c"), Hash("ES", "a", "bc"));
    }

    [Fact]
    public void TheHashIsSixtyFourHexCharacters()
    {
        Assert.Matches("^[0-9a-f]{64}$", Hash("ES", "[1]", "[2]"));
    }

    private static string Hash(string country, string regions, string cities) =>
        RegionSeedHash.Compute(country, ToStream(regions), ToStream(cities));

    private static MemoryStream ToStream(string text) => new(Encoding.UTF8.GetBytes(text));
}
