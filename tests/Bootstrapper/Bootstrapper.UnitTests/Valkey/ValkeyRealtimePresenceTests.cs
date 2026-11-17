using System.Diagnostics.Metrics;
using Bootstrapper.Realtime;
using Bootstrapper.UnitTests.Realtime;
using Bootstrapper.Valkey;
using Bootstrapper.Valkey.Presence;
using BuildingBlocks.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bootstrapper.UnitTests.Valkey;

/// <summary>
/// Spec 2026-11-15 §5.3 and plan N11: ticket presence shared between pods in <c>qep:rt:presence:{tenantId}:{topic}</c>, under the
/// production ACL (no Lua: trimming and reading are two plain commands). Same behaviour as the local registry, plus the key's TTL and
/// the fallback to the local registry when Valkey fails. A new tenant per test, so tests do not cross in the class container.
/// </summary>
public sealed class ValkeyRealtimePresenceTests(ValkeyAclServer server) : IClassFixture<ValkeyAclServer>
{
    private const string Topic = "support:ticket:0190a000-0000-7000-8000-000000000001";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ComposingAppearsAndNoneRemovesOnlyThatConnection()
    {
        await using var pod = await Pod.StartAsync(ConnectionString(), new ManualClock());
        var tenant = Guid.CreateVersion7();
        var ana = Guid.CreateVersion7();
        var bruno = Guid.CreateVersion7();

        await pod.Presence.SetComposingAsync(tenant, Topic, ana, "ana-1", Ct);
        var both = await pod.Presence.SetComposingAsync(tenant, Topic, bruno, "bruno-1", Ct);
        var afterClear = await pod.Presence.ClearAsync(tenant, Topic, ana, "ana-1", Ct);

        Assert.Equal(new[] { ana, bruno }.Order(), both.Order());
        Assert.Equal([bruno], afterClear);
        Assert.Equal([bruno], await pod.Presence.ReadAsync(tenant, Topic, Ct));
    }

    [Fact]
    public async Task TwoConnectionsOfOneMemberCountOnce()
    {
        await using var pod = await Pod.StartAsync(ConnectionString(), new ManualClock());
        var tenant = Guid.CreateVersion7();
        var ana = Guid.CreateVersion7();

        await pod.Presence.SetComposingAsync(tenant, Topic, ana, "tab-a", Ct);
        var twoTabs = await pod.Presence.SetComposingAsync(tenant, Topic, ana, "tab-b", Ct);
        var oneTabLeft = await pod.Presence.ClearAsync(tenant, Topic, ana, "tab-a", Ct);

        Assert.Equal([ana], twoTabs);
        Assert.Equal([ana], oneTabLeft);
    }

    [Fact]
    public async Task AnEntryExpiresAfter45Seconds()
    {
        var clock = new ManualClock();
        await using var pod = await Pod.StartAsync(ConnectionString(), clock);
        var tenant = Guid.CreateVersion7();
        var ana = Guid.CreateVersion7();
        await pod.Presence.SetComposingAsync(tenant, Topic, ana, "ana-1", Ct);

        clock.Advance(TimeSpan.FromSeconds(44));
        Assert.Equal([ana], await pod.Presence.ReadAsync(tenant, Topic, Ct));

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Empty(await pod.Presence.ReadAsync(tenant, Topic, Ct));
    }

    [Fact]
    public async Task RenewingExtendsTheEntry()
    {
        var clock = new ManualClock();
        await using var pod = await Pod.StartAsync(ConnectionString(), clock);
        var tenant = Guid.CreateVersion7();
        var ana = Guid.CreateVersion7();
        await pod.Presence.SetComposingAsync(tenant, Topic, ana, "ana-1", Ct);

        clock.Advance(TimeSpan.FromSeconds(20));
        await pod.Presence.SetComposingAsync(tenant, Topic, ana, "ana-1", Ct);
        clock.Advance(TimeSpan.FromSeconds(40));

        Assert.Equal([ana], await pod.Presence.ReadAsync(tenant, Topic, Ct));
    }

    [Fact]
    public async Task TheKeyIsTheRealtimePresenceKeyAndExpiresIn60Seconds()
    {
        await using var pod = await Pod.StartAsync(ConnectionString(), new ManualClock());
        var tenant = Guid.CreateVersion7();

        await pod.Presence.SetComposingAsync(tenant, Topic, Guid.CreateVersion7(), "ana-1", Ct);

        var key = ValkeyKeys.RealtimePresence(tenant, Topic);
        Assert.Equal($"qep:rt:presence:{tenant:D}:{Topic}", key.ToString());
        var ttl = await pod.Database.KeyTimeToLiveAsync(key);
        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(60));
    }

    // Own container: stopping it would leave the rest of the class without Valkey.
    [Fact]
    public async Task WhenValkeyFailsItFallsBackToTheLocalRegistry()
    {
        await using var container = await ValkeyTestServer.StartAsync(withProductionAcl: false, Ct);
        await using var pod = await Pod.StartAsync(ValkeyTestServer.ConnectionString(container, asQep: false), new ManualClock());
        var tenant = Guid.CreateVersion7();
        var ana = Guid.CreateVersion7();
        await pod.Presence.SetComposingAsync(tenant, Topic, ana, "ana-1", Ct);

        await container.StopAsync(Ct);
        var before = pod.Fallbacks.Count("presence");
        var bruno = Guid.CreateVersion7();

        var members = await pod.Presence.SetComposingAsync(tenant, Topic, bruno, "bruno-1", Ct);

        Assert.Equal(new[] { ana, bruno }.Order(), members.Order());
        Assert.True(pod.Fallbacks.Count("presence") > before);
    }

    private string ConnectionString() => ValkeyTestServer.ConnectionString(server.Container, asQep: true);

    /// <summary>One pod: its multiplexer, its local registry and the Valkey presence, as <c>AddQepValkey</c> wires them.</summary>
    private sealed class Pod : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly ValkeyConnection _connection;

        private Pod(ServiceProvider provider, ValkeyConnection connection, ValkeyRealtimePresence presence, FallbackCounter fallbacks)
        {
            _provider = provider;
            _connection = connection;
            Presence = presence;
            Fallbacks = fallbacks;
        }

        public ValkeyRealtimePresence Presence { get; }

        public FallbackCounter Fallbacks { get; }

        public StackExchange.Redis.IDatabase Database => _connection.Multiplexer!.GetDatabase();

        public static async Task<Pod> StartAsync(string connectionString, TimeProvider clock)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Valkey"] = connectionString })
                .Build();
            var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
            var factory = provider.GetRequiredService<IMeterFactory>();
            var options = Options.Create(new ValkeyOptions { Enabled = true, Areas = { Presence = true } });
            var connection = new ValkeyConnection(configuration, options, NullLogger<ValkeyConnection>.Instance);
            var fallbacks = new FallbackCounter(factory);
            var gateway = new ValkeyGateway(
                connection, options, new ValkeyMetrics(factory, connection), NullLogger<ValkeyGateway>.Instance);
            await connection.StartAsync(Ct);
            return new Pod(
                provider, connection, new ValkeyRealtimePresence(gateway, new LocalRealtimePresence(clock), clock), fallbacks);
        }

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync();
            Fallbacks.Dispose();
            await _provider.DisposeAsync();
        }
    }

    /// <summary>Counts <c>qep.valkey.fallbacks</c> per area, only from this pod's meter factory.</summary>
    private sealed class FallbackCounter : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly Dictionary<string, long> _counts = [];

        public FallbackCounter(IMeterFactory factory)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter.Scope, factory)
                    && instrument.Meter.Name == QepObservability.MeterName
                    && instrument.Name == "qep.valkey.fallbacks")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag is { Key: "area", Value: string area })
                    {
                        lock (_counts)
                        {
                            _counts[area] = _counts.GetValueOrDefault(area) + value;
                        }
                    }
                }
            });
            _listener.Start();
        }

        public long Count(string area)
        {
            lock (_counts)
            {
                return _counts.GetValueOrDefault(area);
            }
        }

        public void Dispose() => _listener.Dispose();
    }
}
