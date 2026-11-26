using Modules.Hr.Application;

namespace Modules.Hr.UnitTests;

public sealed class HrCityIndexTests
{
    private static readonly Guid Antioquia = Guid.Parse("01900000-0000-7000-8000-00000000a001");
    private static readonly Guid Santander = Guid.Parse("01900000-0000-7000-8000-00000000a002");
    private static readonly Guid Valle = Guid.Parse("01900000-0000-7000-8000-00000000a003");
    private static readonly Guid Medellin = Guid.Parse("01900000-0000-7000-8000-00000000b001");
    private static readonly Guid BarbosaAntioquia = Guid.Parse("01900000-0000-7000-8000-00000000b002");
    private static readonly Guid BarbosaSantander = Guid.Parse("01900000-0000-7000-8000-00000000b003");
    private static readonly Guid Cali = Guid.Parse("01900000-0000-7000-8000-00000000b004");

    private static HrCityIndex Index() => new(
    [
        new HrCityRef(Medellin, "Medellín", "Medellín", Antioquia, "Antioquia"),
        new HrCityRef(BarbosaAntioquia, "Barbosa", "Barbosa", Antioquia, "Antioquia"),
        new HrCityRef(BarbosaSantander, "Barbosa", "Barbosa", Santander, "Santander"),
        new HrCityRef(Cali, "Cali", "Santiago de Cali", Valle, "Valle del Cauca"),
    ]);

    [Fact]
    public void MatchesWithoutAccentsOrCase()
    {
        var match = Index().Match("  MEDELLIN ");

        Assert.Equal(HrCityOutcome.Found, match.Outcome);
        Assert.Equal(Medellin, match.CityId);
    }

    [Fact]
    public void TheDivipolaNameAlsoMatches()
    {
        var match = Index().Match("santiago de cali");

        Assert.Equal(HrCityOutcome.Found, match.Outcome);
        Assert.Equal(Cali, match.CityId);
    }

    [Fact]
    public void ARepeatedNameIsAmbiguous()
    {
        var match = Index().Match("Barbosa");

        Assert.Equal(HrCityOutcome.Ambiguous, match.Outcome);
        Assert.Null(match.CityId);
    }

    [Fact]
    public void TheDepartmentResolvesTheAmbiguity()
    {
        var match = Index().Match("Barbosa - Santander");

        Assert.Equal(HrCityOutcome.Found, match.Outcome);
        Assert.Equal(BarbosaSantander, match.CityId);
    }

    [Fact]
    public void AnUnknownCityIsNotFound()
    {
        Assert.Equal(HrCityOutcome.NotFound, Index().Match("Atlantis").Outcome);
        Assert.Equal(HrCityOutcome.NotFound, Index().Match("Barbosa - Valle del Cauca").Outcome);
    }
}
