using System.Diagnostics.Metrics;
using Bootstrapper.Valkey;
using Bootstrapper.Valkey.Presence;
using BuildingBlocks.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Modules.Messaging.Application;

namespace Bootstrapper.UnitTests.Valkey;

/// <summary>
/// La presencia compartida de la bandeja (spec 2026-11-13, D-2; P-3): cada pod escribe a sus conectados en
/// <c>qep:presence:{tenantId}</c> y el lector ve los de todos. Un tenant nuevo por prueba, así las pruebas no se cruzan en
/// el contenedor de la clase; el reloj es manual porque la ventana de lectura se mide con él. Las escrituras de
/// <c>Connected</c>/<c>Disconnected</c> no se esperan (no bloquean el hub), así que se comprueban sondeando hasta 2 s.
/// </summary>
public sealed class ValkeyPresenceTests(ValkeyPlainServer server) : IClassFixture<ValkeyPlainServer>
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task TwoProcessesSeeEachOthersMembers()
    {
        var clock = new PresenceClock();
        await using var podA = await Pod.StartAsync(ConnectionString(), clock);
        await using var podB = await Pod.StartAsync(ConnectionString(), clock);
        var tenant = Guid.NewGuid();
        var m1 = Guid.NewGuid();
        var m2 = Guid.NewGuid();

        podA.Presence.Connected("c1", tenant, m1);
        podB.Presence.Connected("c2", tenant, m2);

        var members = await PollAsync(podA.Reader, tenant, found => found.Count == 2);
        Assert.Equal(new[] { m1, m2 }.Order(), members.Order());
    }

    [Fact]
    public async Task AMemberWithoutHeartbeatDisappearsAfterNinetySeconds()
    {
        var clock = new PresenceClock();
        await using var pod = await Pod.StartAsync(ConnectionString(), clock);
        var tenant = Guid.NewGuid();
        var member = Guid.NewGuid();
        pod.Presence.Connected("c1", tenant, member);
        await PollAsync(pod.Reader, tenant, found => found.Count == 1);

        clock.Advance(TimeSpan.FromSeconds(91));

        var connected = await pod.Reader.ListConnectedAsync([tenant], Ct);
        Assert.False(connected.ContainsKey(tenant));
    }

    [Fact]
    public async Task TheHeartbeatKeepsTheMemberAndExpiresTheKey()
    {
        var clock = new PresenceClock();
        await using var pod = await Pod.StartAsync(ConnectionString(), clock);
        var tenant = Guid.NewGuid();
        var member = Guid.NewGuid();
        pod.Presence.Connected("c1", tenant, member);
        await PollAsync(pod.Reader, tenant, found => found.Count == 1);

        clock.Advance(TimeSpan.FromSeconds(60));
        await pod.Heartbeat.BeatAsync(Ct);
        clock.Advance(TimeSpan.FromSeconds(60));

        var connected = await pod.Reader.ListConnectedAsync([tenant], Ct);
        Assert.Equal([member], connected[tenant]);
        var ttl = await pod.Database.KeyTimeToLiveAsync(ValkeyKeys.Presence(tenant));
        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(120));
    }

    // Review Focus 5: cerrar una pestaña no saca al miembro; cerrar la última sí.
    [Fact]
    public async Task DisconnectingTheLastLocalConnectionRemovesTheMember()
    {
        var clock = new PresenceClock();
        await using var pod = await Pod.StartAsync(ConnectionString(), clock);
        var tenant = Guid.NewGuid();
        var member = Guid.NewGuid();
        pod.Presence.Connected("tab-1", tenant, member);
        pod.Presence.Connected("tab-2", tenant, member);
        await PollAsync(pod.Reader, tenant, found => found.Count == 1);

        pod.Presence.Disconnected("tab-1");
        // Sin escritura que esperar: se da el mismo plazo que a un ZREM antes de mirar.
        await Task.Delay(300, Ct);
        Assert.Equal([member], (await pod.Reader.ListConnectedAsync([tenant], Ct))[tenant]);

        pod.Presence.Disconnected("tab-2");
        await PollAsync(pod.Reader, tenant, found => found.Count == 0);
    }

    // Review Focus 5: si otro pod lo saca, vuelve en el siguiente latido del pod donde sigue conectado.
    [Fact]
    public async Task AMemberStillConnectedElsewhereReturnsAfterTheNextHeartbeat()
    {
        var clock = new PresenceClock();
        await using var podA = await Pod.StartAsync(ConnectionString(), clock);
        await using var podB = await Pod.StartAsync(ConnectionString(), clock);
        var tenant = Guid.NewGuid();
        var member = Guid.NewGuid();
        podA.Presence.Connected("a", tenant, member);
        podB.Presence.Connected("b", tenant, member);
        await PollAsync(podA.Reader, tenant, found => found.Count == 1);

        podA.Presence.Disconnected("a");
        await PollAsync(podA.Reader, tenant, found => found.Count == 0);

        await podB.Heartbeat.BeatAsync(Ct);

        Assert.Equal([member], (await podA.Reader.ListConnectedAsync([tenant], Ct))[tenant]);
    }

    // Contenedor propio: detenerlo dejaría sin Valkey al resto de las pruebas de la clase.
    [Fact]
    public async Task WithValkeyDownTheReaderReturnsTheLocalRegistry()
    {
        await using var container = await ValkeyTestServer.StartAsync(withProductionAcl: false, Ct);
        var clock = new PresenceClock();
        var readerLogs = new CapturingLogger<ValkeyMessagingPresenceReader>();
        await using var pod = await Pod.StartAsync(
            ValkeyTestServer.ConnectionString(container, asQep: false), clock, readerLogs);
        var tenant = Guid.NewGuid();
        var other = Guid.NewGuid();
        var member = Guid.NewGuid();
        pod.Presence.Connected("c1", tenant, member);
        pod.Presence.Connected("c2", other, Guid.NewGuid());
        await PollAsync(pod.Reader, tenant, found => found.Count == 1);

        await container.StopAsync(Ct);
        var before = pod.Fallbacks.Count("presence");

        var connected = await pod.Reader.ListConnectedAsync([tenant], Ct);

        Assert.Equal([tenant], connected.Keys);
        Assert.Equal([member], connected[tenant]);
        Assert.Equal(before + 1, pod.Fallbacks.Count("presence"));

        // D-2 pide el Warning; no lleva ids ni el host (las excepciones de StackExchange.Redis traen host:puerto).
        var warning = Assert.Single(readerLogs.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Equal(1, warning.EventId.Id);
        Assert.DoesNotContain(tenant.ToString("D"), warning.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(tenant.ToString("N"), warning.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(container.Hostname, warning.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string ConnectionString() => ValkeyTestServer.ConnectionString(server.Container, asQep: false);

    private static async Task<IReadOnlyList<Guid>> PollAsync(
        ValkeyMessagingPresenceReader reader, Guid tenant, Func<IReadOnlyList<Guid>, bool> done)
    {
        var deadline = DateTime.UtcNow + PollTimeout;
        while (true)
        {
            var connected = await reader.ListConnectedAsync([tenant], Ct);
            var members = connected.TryGetValue(tenant, out var found) ? found : [];
            if (done(members) || DateTime.UtcNow >= deadline)
            {
                Assert.True(done(members), $"La presencia no llegó al estado esperado en {PollTimeout}: {members.Count} miembros.");
                return members;
            }

            await Task.Delay(50, Ct);
        }
    }

    /// <summary>Un pod: su multiplexer, su registro local, el publicador, el latido y el lector, como los arma <c>AddQepValkey</c>.</summary>
    private sealed class Pod : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly ValkeyConnection _connection;

        private Pod(
            ServiceProvider provider,
            ValkeyConnection connection,
            ValkeyMessagingPresence presence,
            PresenceHeartbeatWorker heartbeat,
            ValkeyMessagingPresenceReader reader,
            FallbackCounter fallbacks)
        {
            _provider = provider;
            _connection = connection;
            Presence = presence;
            Heartbeat = heartbeat;
            Reader = reader;
            Fallbacks = fallbacks;
        }

        public ValkeyMessagingPresence Presence { get; }

        public PresenceHeartbeatWorker Heartbeat { get; }

        public ValkeyMessagingPresenceReader Reader { get; }

        public FallbackCounter Fallbacks { get; }

        public StackExchange.Redis.IDatabase Database => _connection.Multiplexer!.GetDatabase();

        public static async Task<Pod> StartAsync(
            string connectionString, TimeProvider clock, ILogger<ValkeyMessagingPresenceReader>? readerLogger = null)
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

            var local = new MessagingPresence();
            return new Pod(
                provider,
                connection,
                new ValkeyMessagingPresence(local, gateway, clock),
                new PresenceHeartbeatWorker(local, gateway, clock),
                new ValkeyMessagingPresenceReader(
                    gateway,
                    new LocalMessagingPresenceReader(local),
                    clock,
                    readerLogger ?? NullLogger<ValkeyMessagingPresenceReader>.Instance),
                fallbacks);
        }

        public async ValueTask DisposeAsync()
        {
            Heartbeat.Dispose();
            await _connection.DisposeAsync();
            Fallbacks.Dispose();
            await _provider.DisposeAsync();
        }
    }

    /// <summary>Cuenta <c>qep.valkey.fallbacks</c> por área, sólo del factory de este pod.</summary>
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

    /// <summary>Guarda nivel, id de evento y mensaje ya formateado de cada log de una sola categoría.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, EventId EventId, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, EventId EventId, string Message)> Entries
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add((logLevel, eventId, $"{formatter(state, exception)} {exception}"));
            }
        }
    }

    /// <summary>Un reloj de pared que sólo avanza cuando la prueba lo mueve; arranca en la hora real.</summary>
    private sealed class PresenceClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan span) => _now += span;
    }
}
