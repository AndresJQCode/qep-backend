using Modules.Support.Application;

namespace Modules.Support.UnitTests;

public sealed class SupportRealtimeTopicsTests
{
    [Fact]
    public void TheTopicItWritesParsesBack()
    {
        var id = Guid.CreateVersion7();

        Assert.True(SupportRealtimeTopics.TryParseTicket(SupportRealtimeTopics.Ticket(id), out var parsed));
        Assert.Equal(id, parsed);
    }

    // An uppercase Guid names the same ticket but another SignalR group and presence key: two tabs would not see each other.
    [Fact]
    public void AnUppercaseTicketTopicIsNotATicketTopic()
    {
        var id = Guid.CreateVersion7();
        var upper = "support:ticket:" + id.ToString("D").ToUpperInvariant();

        Assert.False(SupportRealtimeTopics.TryParseTicket(upper, out var parsed));
        Assert.Equal(Guid.Empty, parsed);
    }

    [Theory]
    [InlineData("support:ticket:")]
    [InlineData("support:ticket:no-es-guid")]
    [InlineData("support:ticket:{00000000-0000-0000-0000-000000000001}")]
    [InlineData("support:ticket:00000000000000000000000000000001")]
    [InlineData("support")]
    public void AnythingElseIsNotATicketTopic(string topic)
    {
        Assert.False(SupportRealtimeTopics.TryParseTicket(topic, out _));
    }
}
