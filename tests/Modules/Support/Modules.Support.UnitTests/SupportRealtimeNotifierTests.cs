using BuildingBlocks.Application.Realtime;
using Microsoft.Extensions.Logging.Abstractions;
using Modules.Support.Application;
using Modules.Support.Infrastructure;

namespace Modules.Support.UnitTests;

// Spec 2026-11-22 §5.4: the task topics go out first and one failed publish never stops the rest (ids-only payloads).
public sealed class SupportRealtimeNotifierTests
{
    private static readonly Guid TenantId = Guid.Parse("0193a000-0000-7000-8000-000000000001");
    private static readonly Guid TicketId = Guid.Parse("0193a000-0000-7000-8000-0000000000b1");
    private static readonly Guid Creator = Guid.Parse("0193a000-0000-7000-8000-0000000000a1");
    private static readonly Guid Assignee = Guid.Parse("0193a000-0000-7000-8000-0000000000a2");

    private sealed class RecordingPublisher(string? failingTopic = null) : IRealtimePublisher
    {
        public List<(string Topic, string Kind, object Payload)> Published { get; } = [];

        public Task PublishAsync(Guid tenantId, string topic, string kind, object payload, CancellationToken cancellationToken)
        {
            Published.Add((topic, kind, payload));
            return topic == failingTopic ? throw new InvalidOperationException("boom") : Task.CompletedTask;
        }
    }

    private static SupportRealtimeNotifier Notifier(RecordingPublisher publisher) => new(publisher, NullLogger<SupportRealtimeNotifier>.Instance);

    [Fact]
    public async Task TaskTopicsGoOutBeforeTheTicketAndTheInbox()
    {
        var publisher = new RecordingPublisher();

        await Notifier(publisher).TaskChangedAsync(TenantId, TicketId, [Creator, Assignee, Creator], TestContext.Current.CancellationToken);

        Assert.Equal(
            [SupportRealtimeTopics.Tasks(Creator), SupportRealtimeTopics.Tasks(Assignee), SupportRealtimeTopics.Ticket(TicketId), SupportRealtimeTopics.Inbox],
            publisher.Published.Select(item => item.Topic));
        // The payload is the id only (an anonymous type of the production assembly, so it is read by reflection).
        Assert.All(publisher.Published, item =>
        {
            var property = Assert.Single(item.Payload.GetType().GetProperties());
            Assert.Equal(("ticketId", TicketId), (property.Name, property.GetValue(item.Payload)));
        });
    }

    [Fact]
    public async Task OneFailedPublishDoesNotStopTheRest()
    {
        var publisher = new RecordingPublisher(failingTopic: SupportRealtimeTopics.Tasks(Creator));

        await Notifier(publisher).TaskChangedAsync(TenantId, TicketId, [Creator, Assignee], TestContext.Current.CancellationToken);

        Assert.Equal(4, publisher.Published.Count);
        Assert.Contains(publisher.Published, item => item.Topic == SupportRealtimeTopics.Tasks(Assignee));
        Assert.Contains(publisher.Published, item => item.Topic == SupportRealtimeTopics.Inbox);
    }

    [Fact]
    public async Task ATicketChangeStillGoesToTheTicketAndTheInbox()
    {
        var publisher = new RecordingPublisher();

        await Notifier(publisher).TicketChangedAsync(TenantId, TicketId, TestContext.Current.CancellationToken);

        Assert.Equal([SupportRealtimeTopics.Ticket(TicketId), SupportRealtimeTopics.Inbox], publisher.Published.Select(item => item.Topic));
    }
}
