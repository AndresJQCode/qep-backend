using Modules.Messaging.Application;

namespace Modules.Messaging.UnitTests;

/// <summary>Registro de quién tiene abierta una conexión de la bandeja: una conexión por pestaña, un miembro cuenta una sola vez.</summary>
public sealed class MessagingPresenceTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid Member = Guid.CreateVersion7();

    [Fact]
    public void TwoConnectionsOfOneMemberCountOnce()
    {
        var presence = new MessagingPresence();
        presence.Connected("a", Tenant, Member);
        presence.Connected("b", Tenant, Member);

        var snapshot = presence.Snapshot();

        Assert.Equal([Member], snapshot[Tenant]);
    }

    [Fact]
    public void ClosingOneConnectionKeepsTheMember()
    {
        var presence = new MessagingPresence();
        presence.Connected("a", Tenant, Member);
        presence.Connected("b", Tenant, Member);

        presence.Disconnected("a");

        Assert.Equal([Member], presence.Snapshot()[Tenant]);
    }

    [Fact]
    public void ClosingTheLastConnectionRemovesTheMember()
    {
        var presence = new MessagingPresence();
        presence.Connected("a", Tenant, Member);

        presence.Disconnected("a");

        Assert.Empty(presence.Snapshot());
    }

    [Fact]
    public void ClosingAnUnknownConnectionDoesNothing()
    {
        var presence = new MessagingPresence();
        presence.Connected("a", Tenant, Member);

        presence.Disconnected("nobody");

        Assert.Equal([Member], presence.Snapshot()[Tenant]);
    }

    [Fact]
    public void ANewConnectionBeforeTheOldOneClosesKeepsTheMember()
    {
        var presence = new MessagingPresence();
        presence.Connected("old", Tenant, Member);
        presence.Connected("new", Tenant, Member);
        presence.Disconnected("old");

        Assert.Equal([Member], presence.Snapshot()[Tenant]);
    }

    [Fact]
    public async Task TheLocalReaderReturnsOnlyTheRequestedTenants()
    {
        var other = Guid.CreateVersion7();
        var presence = new MessagingPresence();
        presence.Connected("a", Tenant, Member);
        presence.Connected("b", other, Guid.CreateVersion7());
        var reader = new LocalMessagingPresenceReader(presence);

        var connected = await reader.ListConnectedAsync([Tenant], TestContext.Current.CancellationToken);

        Assert.Equal([Tenant], connected.Keys);
        Assert.Equal([Member], connected[Tenant]);
    }

    [Fact]
    public async Task TheLocalReaderDeduplicatesTabs()
    {
        var presence = new MessagingPresence();
        presence.Connected("a", Tenant, Member);
        presence.Connected("b", Tenant, Member);
        var reader = new LocalMessagingPresenceReader(presence);

        var connected = await reader.ListConnectedAsync([Tenant], TestContext.Current.CancellationToken);

        Assert.Equal([Member], connected[Tenant]);
    }

    [Fact]
    public void TryRemoveReturnsTheOwnerOfTheConnection()
    {
        var presence = new MessagingPresence();
        presence.Connected("a", Tenant, Member);

        Assert.True(presence.TryRemove("a", out var tenantId, out var memberId));
        Assert.Equal((Tenant, Member), (tenantId, memberId));
        Assert.False(presence.TryRemove("nobody", out _, out _));
    }

    [Fact]
    public void HasConnectionSeesTheOtherTab()
    {
        var presence = new MessagingPresence();
        presence.Connected("a", Tenant, Member);
        presence.Connected("b", Tenant, Member);

        presence.TryRemove("a", out _, out _);
        Assert.True(presence.HasConnection(Tenant, Member));

        presence.TryRemove("b", out _, out _);
        Assert.False(presence.HasConnection(Tenant, Member));
    }
}
