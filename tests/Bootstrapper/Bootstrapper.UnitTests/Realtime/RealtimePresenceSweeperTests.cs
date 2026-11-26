using Bootstrapper.Realtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootstrapper.UnitTests.Realtime;

/// <summary>
/// Plan N10 (Review Focus 3): an entry that nobody renews and nobody clears (a pod that died, a cut network) disappears from what the
/// other members see without any disconnect. The sweeper rereads the groups whose last delivered photo was not empty and delivers
/// again only when it changed.
/// </summary>
public sealed class RealtimePresenceSweeperTests
{
    private const string Topic = "support:ticket:0190a000-0000-7000-8000-000000000001";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheSweeperDropsAnExpiredEntryWithoutADisconnect()
    {
        var rig = Rig.Start();
        await rig.Presence.SetComposingAsync(rig.Tenant, Topic, rig.Ana, "ana-elsewhere", Ct);
        await rig.Dispatcher.DeliverPresenceAsync(rig.Tenant, Topic, [new PresenceMember(rig.Ana, "Ana", RealtimePresence.Composing)], Ct);
        var delivered = rig.Hub.Sent.Count;

        rig.Clock.Advance(TimeSpan.FromSeconds(46));
        await rig.Sweeper.SweepOnceAsync(Ct);

        var sent = Assert.Single(rig.Hub.Sent.Skip(delivered));
        Assert.Equal("bruno-1", sent.ConnectionId);
        Assert.Equal(AppHubEvents.PresenceChanged, sent.Method);
        Assert.Equal(Topic, sent.Payload.GetProperty("topic").GetString());
        Assert.Empty(sent.Payload.GetProperty("members").EnumerateArray());
    }

    [Fact]
    public async Task ANonChangingGroupIsNotResent()
    {
        var rig = Rig.Start();
        await rig.Presence.SetComposingAsync(rig.Tenant, Topic, rig.Ana, "ana-elsewhere", Ct);
        await rig.Dispatcher.DeliverPresenceAsync(rig.Tenant, Topic, [new PresenceMember(rig.Ana, "Ana", RealtimePresence.Composing)], Ct);
        var delivered = rig.Hub.Sent.Count;

        rig.Clock.Advance(TimeSpan.FromSeconds(10));
        await rig.Sweeper.SweepOnceAsync(Ct);

        Assert.Equal(delivered, rig.Hub.Sent.Count);
    }

    /// <summary>Bruno is connected to this pod and subscribed to the ticket; Ana writes from another pod.</summary>
    private sealed record Rig(
        ManualClock Clock,
        LocalRealtimePresence Presence,
        RecordingHubContext Hub,
        AppHubDispatcher Dispatcher,
        RealtimePresenceSweeper Sweeper,
        Guid Tenant,
        Guid Ana)
    {
        public static Rig Start()
        {
            var clock = new ManualClock();
            var presence = new LocalRealtimePresence(clock);
            var hub = new RecordingHubContext();
            var connections = new AppHubConnections();
            var tenant = Guid.CreateVersion7();
            connections.Register("bruno-1", tenant, Guid.CreateVersion7());
            Assert.True(connections.TryAddTopic("bruno-1", Topic));
            var dispatcher = new AppHubDispatcher(hub, connections);
            var services = new ServiceCollection().AddScoped<IRealtimeMemberNames, FixedMemberNames>().BuildServiceProvider();
            var sweeper = new RealtimePresenceSweeper(
                presence, dispatcher, services.GetRequiredService<IServiceScopeFactory>(), NullLogger<RealtimePresenceSweeper>.Instance);
            return new Rig(clock, presence, hub, dispatcher, sweeper, tenant, Guid.CreateVersion7());
        }
    }
}
