using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Persistence;

namespace Modules.Messaging.UnitTests;

/// <summary><c>parameters jsonb</c>: una fuente que el parser no conozca se descarta en silencio y la campaña se detendría por variables que
/// no calzan, así que cada una tiene que hacer ida y vuelta.</summary>
public sealed class CampaignParametersJsonTests
{
    [Fact]
    public void TheGreetingRoundTripsWithoutAValue()
    {
        var json = CampaignParametersJson.Serialize(new Dictionary<string, CampaignParameter> { ["1"] = CampaignParameter.Greeting });

        Assert.Equal("""{"1":{"source":"greeting"}}""", json);
        Assert.Equal(CampaignParameter.Greeting, CampaignParametersJson.Parse(json)["1"]);
    }
}
