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

    // Spec 2026-11-22 (plan D1): the tasks topic of one member. Colons, not dots: the hub picks the authorizer by the text before the
    // first colon, and a dotted topic would be its own prefix and nobody would authorize it.
    [Fact]
    public void TheTasksTopicRoundTrips()
    {
        var member = Guid.CreateVersion7();

        Assert.Equal($"support:tasks:{member:D}", SupportRealtimeTopics.Tasks(member));
        Assert.True(SupportRealtimeTopics.TryParseTasks(SupportRealtimeTopics.Tasks(member), out var parsed));
        Assert.Equal(member, parsed);
    }

    // An uppercase Guid names the same member but another SignalR group: only the exact form is a topic.
    [Theory]
    [InlineData("support")]
    [InlineData("support:tasks:")]
    [InlineData("support:tasks:no-es-guid")]
    [InlineData("support:tasks:{00000000-0000-0000-0000-000000000001}")]
    [InlineData("support:tasks:00000000000000000000000000000001")]
    [InlineData("support:tasks:0193A000-0000-7000-8000-0000000000A1")]
    [InlineData("support:ticket:0193a000-0000-7000-8000-0000000000a1")]
    public void TryParseTasksRefusesUppercaseInboxAndTicketTopics(string topic)
    {
        Assert.False(SupportRealtimeTopics.TryParseTasks(topic, out var parsed));
        Assert.Equal(Guid.Empty, parsed);
    }

    [Fact]
    public void ATasksTopicIsNotATicketTopic() =>
        Assert.False(SupportRealtimeTopics.TryParseTicket(SupportRealtimeTopics.Tasks(Guid.CreateVersion7()), out _));
}
