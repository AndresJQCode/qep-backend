using Bootstrapper.Realtime;

namespace Bootstrapper.UnitTests.Realtime;

/// <summary>Spec 2026-11-15 §5.3 and plan N11: the pod-local presence (Valkey off or down). Per connection, never per member; an entry
/// lasts 45 s unless renewed.</summary>
public sealed class LocalRealtimePresenceTests
{
    private const string Topic = "support:ticket:0190a000-0000-7000-8000-000000000001";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ComposingAppearsAndNoneRemovesOnlyThatConnection()
    {
        var presence = new LocalRealtimePresence(new ManualClock());
        var tenant = Guid.CreateVersion7();
        var ana = Guid.CreateVersion7();
        var bruno = Guid.CreateVersion7();

        await presence.SetComposingAsync(tenant, Topic, ana, "ana-1", Ct);
        var both = await presence.SetComposingAsync(tenant, Topic, bruno, "bruno-1", Ct);
        var afterClear = await presence.ClearAsync(tenant, Topic, ana, "ana-1", Ct);

        Assert.Equal(new[] { ana, bruno }.Order(), both.Order());
        Assert.Equal([bruno], afterClear);
        Assert.Equal([bruno], await presence.ReadAsync(tenant, Topic, Ct));
        Assert.Empty(await presence.ReadAsync(Guid.CreateVersion7(), Topic, Ct));
    }

    [Fact]
    public async Task TwoConnectionsOfOneMemberCountOnce()
    {
        var presence = new LocalRealtimePresence(new ManualClock());
        var tenant = Guid.CreateVersion7();
        var ana = Guid.CreateVersion7();

        await presence.SetComposingAsync(tenant, Topic, ana, "tab-a", Ct);
        var twoTabs = await presence.SetComposingAsync(tenant, Topic, ana, "tab-b", Ct);
        var oneTabLeft = await presence.ClearAsync(tenant, Topic, ana, "tab-a", Ct);

        Assert.Equal([ana], twoTabs);
        Assert.Equal([ana], oneTabLeft);
    }

    [Fact]
    public async Task AnEntryExpiresAfter45Seconds()
    {
        var clock = new ManualClock();
        var presence = new LocalRealtimePresence(clock);
        var tenant = Guid.CreateVersion7();
        var ana = Guid.CreateVersion7();
        await presence.SetComposingAsync(tenant, Topic, ana, "ana-1", Ct);

        clock.Advance(TimeSpan.FromSeconds(44));
        Assert.Equal([ana], await presence.ReadAsync(tenant, Topic, Ct));

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Empty(await presence.ReadAsync(tenant, Topic, Ct));
    }

    [Fact]
    public async Task RenewingExtendsTheEntry()
    {
        var clock = new ManualClock();
        var presence = new LocalRealtimePresence(clock);
        var tenant = Guid.CreateVersion7();
        var ana = Guid.CreateVersion7();
        await presence.SetComposingAsync(tenant, Topic, ana, "ana-1", Ct);

        clock.Advance(TimeSpan.FromSeconds(20));
        await presence.SetComposingAsync(tenant, Topic, ana, "ana-1", Ct);
        clock.Advance(TimeSpan.FromSeconds(40));

        Assert.Equal([ana], await presence.ReadAsync(tenant, Topic, Ct));
    }
}
