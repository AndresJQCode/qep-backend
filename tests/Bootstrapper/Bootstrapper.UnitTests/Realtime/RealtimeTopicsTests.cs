using Bootstrapper.Realtime;

namespace Bootstrapper.UnitTests.Realtime;

/// <summary>Spec 2026-11-15 §5.1: the SignalR group of a topic always carries the connection's tenant, so the same topic text in two
/// tenants never shares a group; the authorizer is picked by the text before the first colon.</summary>
public sealed class RealtimeTopicsTests
{
    [Fact]
    public void TheGroupCarriesTheTenant()
    {
        var tenant = Guid.CreateVersion7();

        Assert.Equal($"{tenant:D}:support", RealtimeTopics.GroupOf(tenant, "support"));
    }

    [Theory]
    [InlineData("support:ticket:x", "support")]
    [InlineData("support", "support")]
    [InlineData("nadie:sabe", "nadie")]
    public void ThePrefixIsTheTextBeforeTheFirstColon(string topic, string prefix) =>
        Assert.Equal(prefix, RealtimeTopics.PrefixOf(topic));
}
