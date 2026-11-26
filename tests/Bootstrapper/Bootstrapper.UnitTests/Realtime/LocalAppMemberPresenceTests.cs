using Bootstrapper.Realtime;

namespace Bootstrapper.UnitTests.Realtime;

/// <summary>
/// Spec 2026-11-22 D-1: whether a member has QEP open, read from the pod's own app hub registry. The registry already counts one entry per
/// connection, so closing one of two tabs must keep the member connected and closing the last one must not.
/// </summary>
public sealed class LocalAppMemberPresenceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AConnectedMemberIsConnected()
    {
        var connections = new AppHubConnections();
        var presence = new LocalAppMemberPresence(connections);
        var tenant = Guid.NewGuid();
        var member = Guid.NewGuid();
        connections.Register("tab-1", tenant, member);
        presence.Connected(tenant, member);

        Assert.True(await presence.IsConnectedAsync(tenant, member, Ct));
    }

    [Fact]
    public async Task ClosingTheOnlyTabDisconnects()
    {
        var connections = new AppHubConnections();
        var presence = new LocalAppMemberPresence(connections);
        var tenant = Guid.NewGuid();
        var member = Guid.NewGuid();
        connections.Register("tab-1", tenant, member);

        connections.Remove("tab-1");
        presence.Disconnected(tenant, member);

        Assert.False(await presence.IsConnectedAsync(tenant, member, Ct));
    }

    [Fact]
    public async Task ClosingOneOfTwoTabsKeepsItConnected()
    {
        var connections = new AppHubConnections();
        var presence = new LocalAppMemberPresence(connections);
        var tenant = Guid.NewGuid();
        var member = Guid.NewGuid();
        connections.Register("tab-1", tenant, member);
        connections.Register("tab-2", tenant, member);

        connections.Remove("tab-1");
        presence.Disconnected(tenant, member);

        Assert.True(await presence.IsConnectedAsync(tenant, member, Ct));
    }

    [Fact]
    public async Task AnotherTenantIsNotConnected()
    {
        var connections = new AppHubConnections();
        var presence = new LocalAppMemberPresence(connections);
        var member = Guid.NewGuid();
        connections.Register("tab-1", Guid.NewGuid(), member);

        Assert.False(await presence.IsConnectedAsync(Guid.NewGuid(), member, Ct));
    }
}
