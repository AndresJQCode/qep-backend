using Bootstrapper.Realtime;
using Bootstrapper.Valkey;
using Bootstrapper.Valkey.Presence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bootstrapper.UnitTests.Valkey;

/// <summary>
/// Spec 2026-11-22 D-1: «does this member have QEP open?» across pods, from <c>qep:rt:online:{tenantId}</c>. A new tenant per test so the
/// tests do not cross inside the class container; the clock is manual because the read window is measured with it. <c>Connected</c> and
/// <c>Disconnected</c> are not awaited (they never hold the hub), so what they write is polled for up to 2 s.
/// </summary>
public sealed class ValkeyAppMemberPresenceTests(ValkeyPlainServer server) : IClassFixture<ValkeyPlainServer>
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(2);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TwoPodsSeeEachOthersMembers()
    {
        var clock = new ManualTimeProvider();
        await using var podA = await Pod.StartAsync(ConnectionString(), clock);
        await using var podB = await Pod.StartAsync(ConnectionString(), clock);
        var tenant = Guid.NewGuid();
        var member = Guid.NewGuid();

        podA.Open("a-1", tenant, member);

        await PollAsync(podB, tenant, member, expected: true);
    }

    [Fact]
    public async Task WithoutHeartbeatItIsDisconnectedAfterNinetySeconds()
    {
        var clock = new ManualTimeProvider();
        await using var pod = await Pod.StartAsync(ConnectionString(), clock);
        var tenant = Guid.NewGuid();
        var member = Guid.NewGuid();
        pod.Open("a-1", tenant, member);
        await PollAsync(pod, tenant, member, expected: true);

        clock.Advance(TimeSpan.FromSeconds(91));

        Assert.False(await pod.Presence.IsConnectedAsync(tenant, member, Ct));
    }

    [Fact]
    public async Task TheHeartbeatKeepsItConnected()
    {
        var clock = new ManualTimeProvider();
        await using var pod = await Pod.StartAsync(ConnectionString(), clock);
        var tenant = Guid.NewGuid();
        var member = Guid.NewGuid();
        pod.Open("a-1", tenant, member);
        await PollAsync(pod, tenant, member, expected: true);

        clock.Advance(TimeSpan.FromSeconds(60));
        await pod.Heartbeat.BeatAsync(Ct);
        clock.Advance(TimeSpan.FromSeconds(60));

        Assert.True(await pod.Presence.IsConnectedAsync(tenant, member, Ct));
    }

    [Fact]
    public async Task DisconnectingWithAnotherLocalTabKeepsIt()
    {
        var clock = new ManualTimeProvider();
        await using var pod = await Pod.StartAsync(ConnectionString(), clock);
        var tenant = Guid.NewGuid();
        var member = Guid.NewGuid();
        pod.Open("a-1", tenant, member);
        pod.Open("a-2", tenant, member);
        await PollAsync(pod, tenant, member, expected: true);

        pod.Close("a-1", tenant, member);
        // Nothing to await: give it the time a ZREM would need before looking.
        await Task.Delay(300, Ct);
        Assert.True(await pod.Presence.IsConnectedAsync(tenant, member, Ct));

        pod.Close("a-2", tenant, member);
        await PollAsync(pod, tenant, member, expected: false);
    }

    // Own container: stopping it would leave the rest of the class without Valkey.
    [Fact]
    public async Task WhenValkeyFailsItReadsTheLocalRegistry()
    {
        await using var container = await ValkeyTestServer.StartAsync(withProductionAcl: false, Ct);
        var clock = new ManualTimeProvider();
        await using var pod = await Pod.StartAsync(ValkeyTestServer.ConnectionString(container, asQep: false), clock);
        var tenant = Guid.NewGuid();
        var member = Guid.NewGuid();
        pod.Open("a-1", tenant, member);
        await PollAsync(pod, tenant, member, expected: true);

        await container.StopAsync(Ct);

        Assert.True(await pod.Presence.IsConnectedAsync(tenant, member, Ct));
        Assert.False(await pod.Presence.IsConnectedAsync(tenant, Guid.NewGuid(), Ct));
    }

    private string ConnectionString() => ValkeyTestServer.ConnectionString(server.Container, asQep: false);

    private static async Task PollAsync(Pod pod, Guid tenant, Guid member, bool expected)
    {
        var deadline = DateTime.UtcNow + PollTimeout;
        while (true)
        {
            var connected = await pod.Presence.IsConnectedAsync(tenant, member, Ct);
            if (connected == expected || DateTime.UtcNow >= deadline)
            {
                Assert.True(connected == expected, $"Presence did not reach {expected} within {PollTimeout}.");
                return;
            }

            await Task.Delay(50, Ct);
        }
    }

    /// <summary>One pod: its multiplexer, its app hub registry, the presence and the heartbeat, as <c>AddQepValkey</c> wires them.</summary>
    private sealed class Pod : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly ValkeyConnection _connection;
        private readonly AppHubConnections _connections;

        private Pod(
            ServiceProvider provider,
            ValkeyConnection connection,
            AppHubConnections connections,
            ValkeyAppMemberPresence presence,
            AppPresenceHeartbeatWorker heartbeat)
        {
            _provider = provider;
            _connection = connection;
            _connections = connections;
            Presence = presence;
            Heartbeat = heartbeat;
        }

        public ValkeyAppMemberPresence Presence { get; }

        public AppPresenceHeartbeatWorker Heartbeat { get; }

        public static async Task<Pod> StartAsync(string connectionString, TimeProvider clock)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Valkey"] = connectionString })
                .Build();
            var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
            var factory = provider.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>();
            var options = Options.Create(new ValkeyOptions { Enabled = true, Areas = { Presence = true } });
            var connection = new ValkeyConnection(configuration, options, NullLogger<ValkeyConnection>.Instance);
            var gateway = new ValkeyGateway(
                connection, options, new ValkeyMetrics(factory, connection), NullLogger<ValkeyGateway>.Instance);
            await connection.StartAsync(Ct);

            var connections = new AppHubConnections();
            return new Pod(
                provider,
                connection,
                connections,
                new ValkeyAppMemberPresence(new LocalAppMemberPresence(connections), connections, gateway, clock),
                new AppPresenceHeartbeatWorker(connections, gateway, clock));
        }

        /// <summary>What the hub does on connect: register, then announce.</summary>
        public void Open(string connectionId, Guid tenantId, Guid memberId)
        {
            _connections.Register(connectionId, tenantId, memberId);
            Presence.Connected(tenantId, memberId);
        }

        /// <summary>What the hub does on disconnect: drop from the registry first, so <c>HasMember</c> sees the other tabs.</summary>
        public void Close(string connectionId, Guid tenantId, Guid memberId)
        {
            _connections.Remove(connectionId);
            Presence.Disconnected(tenantId, memberId);
        }

        public async ValueTask DisposeAsync()
        {
            Heartbeat.Dispose();
            await _connection.DisposeAsync();
            await _provider.DisposeAsync();
        }
    }
}
