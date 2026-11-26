using System.Diagnostics;
using System.Diagnostics.Metrics;
using Bootstrapper.Valkey;
using BuildingBlocks.Observability;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bootstrapper.UnitTests.Valkey;

/// <summary>Un Valkey sin ACL, uno para toda la clase.</summary>
public sealed class ValkeyPlainServer : IAsyncLifetime
{
    internal IContainer Container { get; private set; } = null!;

    public async ValueTask InitializeAsync() =>
        Container = await ValkeyTestServer.StartAsync(withProductionAcl: false, TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => Container.DisposeAsync();
}

/// <summary>
/// El gateway de Valkey (spec 2026-11-13, D-1; V3 y V4 del plan): corre la operación con tope, vuelve sin esperar si no hay
/// conexión, cuenta cada respaldo por área y nunca deja la cadena ni el host en un log.
/// </summary>
public sealed class ValkeyGatewayTests(ValkeyPlainServer server) : IClassFixture<ValkeyPlainServer>
{
    [Fact]
    public async Task ADisabledAreaIsNotEnabled()
    {
        await using var harness = await Harness.StartAsync(
            ValkeyTestServer.ConnectionString(server.Container, asQep: false),
            new ValkeyOptions { Enabled = true, Areas = { Presence = false, RateLimiting = true } });

        Assert.False(harness.Gateway.IsEnabled(ValkeyArea.Presence));
        Assert.True(harness.Gateway.IsEnabled(ValkeyArea.RateLimiting));
    }

    [Fact]
    public async Task WithValkeyDisabledNothingConnects()
    {
        await using var harness = await Harness.StartAsync(
            ValkeyTestServer.ConnectionString(server.Container, asQep: false),
            new ValkeyOptions { Enabled = false, Areas = { Presence = true } });

        Assert.Null(harness.Connection.Multiplexer);
        Assert.False(harness.Gateway.IsEnabled(ValkeyArea.Presence));
    }

    [Fact]
    public async Task AnOperationRunsAgainstValkey()
    {
        await using var harness = await Harness.StartAsync(ValkeyTestServer.ConnectionString(server.Container, asQep: false));

        var attempt = await harness.Gateway.TryAsync(
            ValkeyArea.Presence,
            db => db.StringIncrementAsync("qep:t:" + Guid.NewGuid()),
            TestContext.Current.CancellationToken);

        Assert.Equal(new ValkeyAttempt<long>(true, 1), attempt);
        Assert.Equal(0, harness.Recorder.Fallbacks("presence"));
        Assert.Equal("ok", Assert.Single(harness.Recorder.Operations("presence")).Outcome);
    }

    [Fact]
    public async Task AnOperationThatValkeyRejectsRecordsAnError()
    {
        await using var harness = await Harness.StartAsync(ValkeyTestServer.ConnectionString(server.Container, asQep: false));
        var ct = TestContext.Current.CancellationToken;
        var key = "qep:t:" + Guid.NewGuid();
        await harness.Gateway.TryAsync(ValkeyArea.Presence, db => db.StringSetAsync(key, "text"), ct);

        var attempt = await harness.Gateway.TryAsync(ValkeyArea.Sessions, db => db.StringIncrementAsync(key), ct);

        Assert.False(attempt.Succeeded);
        Assert.Equal("error", Assert.Single(harness.Recorder.Operations("sessions")).Outcome);
    }

    [Fact]
    public async Task AClosedPortFallsBackAndCounts()
    {
        await using var harness = await Harness.StartAsync("127.0.0.1:1,connectTimeout=500");

        var attempt = await harness.Gateway.TryAsync(
            ValkeyArea.Presence,
            db => db.StringIncrementAsync("qep:t:" + Guid.NewGuid()),
            TestContext.Current.CancellationToken);

        Assert.False(attempt.Succeeded);
        Assert.Equal(1, harness.Recorder.Fallbacks("presence"));
        Assert.Equal(1, harness.Recorder.Fallbacks("presence", "NotConnected"));
        // Sin conexión no hubo operación: no hay duración que medir.
        Assert.Empty(harness.Recorder.Operations("presence"));
    }

    // Review Focus 3: Valkey lento, no caído. La operación queda colgada y el gateway tiene que soltarla a tiempo.
    [Fact]
    public async Task APausedValkeyFallsBackWithinTheTimeout()
    {
        await using var harness = await Harness.StartAsync(ValkeyTestServer.ConnectionString(server.Container, asQep: false));
        var ct = TestContext.Current.CancellationToken;
        Assert.True((await harness.Gateway.TryAsync(ValkeyArea.RateLimiting, db => db.PingAsync(), ct)).Succeeded);

        await server.Container.ExecAsync(["valkey-cli", "CLIENT", "PAUSE", "2000", "ALL"], ct);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var attempt = await harness.Gateway.TryAsync(
                ValkeyArea.RateLimiting,
                db => db.StringIncrementAsync("qep:t:" + Guid.NewGuid()),
                ct);
            stopwatch.Stop();

            Assert.False(attempt.Succeeded);
            Assert.True(stopwatch.ElapsedMilliseconds < 500, $"Tardó {stopwatch.ElapsedMilliseconds} ms.");
            Assert.Equal(1, harness.Recorder.Fallbacks("rate_limiting"));
            Assert.Equal(1, harness.Recorder.Fallbacks("rate_limiting", "TimeoutException"));
            // La operación soltada sigue viva: todavía no tiene duración (sólo el PING de arriba).
            Assert.Equal("ok", Assert.Single(harness.Recorder.Operations("rate_limiting")).Outcome);
        }
        finally
        {
            await server.Container.ExecAsync(["valkey-cli", "CLIENT", "UNPAUSE"], ct);
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (harness.Recorder.Operations("rate_limiting").Count < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, ct);
        }

        // Lo que se quiere saber en producción: cuánto tardó de verdad la que pasó el tope.
        var late = Assert.Single(harness.Recorder.Operations("rate_limiting"), o => o.Outcome == "late");
        Assert.True(late.Seconds >= 0.1, $"Midió {late.Seconds} s.");
    }

    // Contenedor propio: detenerlo dejaría sin Valkey al resto de las pruebas de la clase.
    [Fact]
    public async Task TheConnectedGaugeFollowsTheConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var container = await ValkeyTestServer.StartAsync(withProductionAcl: false, ct);
        await using var harness = await Harness.StartAsync(ValkeyTestServer.ConnectionString(container, asQep: false));

        Assert.Equal(1, harness.Recorder.Connected());

        await container.StopAsync(ct);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (harness.Recorder.Connected() != 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200, ct);
        }

        Assert.Equal(0, harness.Recorder.Connected());
    }

    // Valkey caído no puede retener el arranque del host: el connect espera hasta ~2 s y sigue en segundo plano. Una IP
    // no enrutable cuelga el SYN (un puerto cerrado respondería al instante y no probaría nada); el connectTimeout de 10 s
    // es lo que el arranque esperaría sin el tope.
    [Fact]
    public async Task AnUnreachableValkeyDoesNotHoldTheStartup()
    {
        var stopwatch = Stopwatch.StartNew();
        await using var harness = await Harness.StartAsync("10.255.255.1:6379,connectTimeout=10000");
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"El arranque tardó {stopwatch.Elapsed}.");
        Assert.False(harness.Connection.Multiplexer?.IsConnected == true);
        var attempt = await harness.Gateway.TryAsync(
            ValkeyArea.RateLimiting, db => db.PingAsync(), TestContext.Current.CancellationToken);
        Assert.False(attempt.Succeeded);
    }

    // Review Focus 2: las excepciones de StackExchange.Redis traen host:puerto en el mensaje.
    [Fact]
    public async Task TheStartupLogNamesTheKeyAndNeverTheValue()
    {
        await using var harness = await Harness.StartAsync(
            "valkey-sentinel-host.invalid:6379,password=SENTINEL-valkey-pw,connectTimeout=500");

        // Una operación sin conexión también deja su rastro (el respaldo en Debug).
        await harness.Gateway.TryAsync(ValkeyArea.Presence, db => db.PingAsync(), TestContext.Current.CancellationToken);

        var logs = harness.Logs.Entries;
        Assert.Contains(logs, entry => entry.Contains("ConnectionStrings:Valkey", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, entry => entry.Contains("SENTINEL-valkey-pw", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, entry => entry.Contains("valkey-sentinel-host", StringComparison.Ordinal));
    }

    // Los timbres (D-7): sin conexión, publicar no espera ni lanza, y cuenta el respaldo del área.
    [Fact]
    public async Task PublishWithoutConnectionCountsAFallbackAndDoesNotThrow()
    {
        await using var harness = await Harness.StartAsync("127.0.0.1:1,connectTimeout=500");

        harness.Gateway.Publish(ValkeyArea.Doorbells, StackExchange.Redis.RedisChannel.Literal("qep:wake:test"));

        Assert.False(harness.Gateway.IsConnected);
        Assert.Equal(1, harness.Recorder.Fallbacks("doorbells"));
    }

    [Fact]
    public async Task TrySubscribeWithoutConnectionReturnsFalse()
    {
        await using var harness = await Harness.StartAsync("127.0.0.1:1,connectTimeout=500");

        var subscribed = await harness.Gateway.TrySubscribeAsync(
            ValkeyArea.Doorbells,
            StackExchange.Redis.RedisChannel.Literal("qep:wake:test"),
            () => { },
            TestContext.Current.CancellationToken);

        Assert.False(subscribed);
        Assert.Equal(1, harness.Recorder.Fallbacks("doorbells"));
    }

    [Fact]
    public void KeysArePrefixedAndLowercase()
    {
        var tenantId = Guid.NewGuid();

        Assert.Equal("qep:rl:credentials:abc:60", (string?)ValkeyKeys.RateLimit("credentials", "ABC", 60));
        Assert.Equal($"qep:presence:{tenantId:D}", (string?)ValkeyKeys.Presence(tenantId));
        Assert.Equal(16, ValkeyKeys.ShortHash("x").Length);
        Assert.Equal(ValkeyKeys.ShortHash("x").ToLowerInvariant(), ValkeyKeys.ShortHash("x"));
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        private Harness(ServiceProvider provider, ValkeyConnection connection, ValkeyGateway gateway, Recorder recorder, CapturingLoggerProvider logs)
        {
            _provider = provider;
            Connection = connection;
            Gateway = gateway;
            Recorder = recorder;
            Logs = logs;
        }

        public ValkeyConnection Connection { get; }

        public ValkeyGateway Gateway { get; }

        public Recorder Recorder { get; }

        public CapturingLoggerProvider Logs { get; }

        public static async Task<Harness> StartAsync(string connectionString, ValkeyOptions? valkeyOptions = null)
        {
            valkeyOptions ??= new ValkeyOptions
            {
                Enabled = true,
                Areas = { Presence = true, RateLimiting = true },
            };
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Valkey"] = connectionString })
                .Build();
            var logs = new CapturingLoggerProvider();
            var provider = new ServiceCollection()
                .AddMetrics()
                .AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs))
                .BuildServiceProvider();
            var factory = provider.GetRequiredService<IMeterFactory>();
            var options = Options.Create(valkeyOptions);
            var loggers = provider.GetRequiredService<ILoggerFactory>();

            var connection = new ValkeyConnection(configuration, options, loggers.CreateLogger<ValkeyConnection>());
            var recorder = new Recorder(factory);
            var metrics = new ValkeyMetrics(factory, connection);
            var gateway = new ValkeyGateway(connection, options, metrics, loggers.CreateLogger<ValkeyGateway>());
            await connection.StartAsync(TestContext.Current.CancellationToken);
            return new Harness(provider, connection, gateway, recorder, logs);
        }

        public async ValueTask DisposeAsync()
        {
            await Connection.StopAsync(CancellationToken.None);
            await Connection.DisposeAsync();
            Recorder.Dispose();
            await _provider.DisposeAsync();
        }
    }

    /// <summary>Molde de <c>QueueGaugesTests</c>: filtra por el factory de la prueba para no cruzarse con otras.</summary>
    private sealed class Recorder : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<(string Instrument, string? Area, string? Reason, long Value)> _measurements = [];

        public Recorder(IMeterFactory factory)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter.Scope, factory) && instrument.Meter.Name == QepObservability.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument, value, tags));
            _listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => Add(instrument, value, tags));
            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => AddDuration(instrument, value, tags));
            _listener.Start();
        }

        private readonly List<(string? Area, string? Outcome, double Seconds)> _operations = [];

        /// <summary>Las duraciones medidas de un área, con su resultado.</summary>
        public IReadOnlyList<(string? Area, string? Outcome, double Seconds)> Operations(string area)
        {
            lock (_operations)
            {
                return [.. _operations.Where(o => o.Area == area)];
            }
        }

        private void AddDuration(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            if (instrument.Name != "qep.valkey.operation.duration")
            {
                return;
            }

            string? area = null;
            string? outcome = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "area")
                {
                    area = tag.Value as string;
                }
                else if (tag.Key == "outcome")
                {
                    outcome = tag.Value as string;
                }
            }

            lock (_operations)
            {
                _operations.Add((area, outcome, value));
            }
        }

        public long Fallbacks(string area)
        {
            lock (_measurements)
            {
                return _measurements.Where(m => m.Instrument == "qep.valkey.fallbacks" && m.Area == area).Sum(m => m.Value);
            }
        }

        /// <summary>Los respaldos de un área con ese motivo (<c>NotConnected</c> o el tipo de la excepción).</summary>
        public long Fallbacks(string area, string reason)
        {
            lock (_measurements)
            {
                return _measurements
                    .Where(m => m.Instrument == "qep.valkey.fallbacks" && m.Area == area && m.Reason == reason)
                    .Sum(m => m.Value);
            }
        }

        public long Connected()
        {
            lock (_measurements)
            {
                _measurements.RemoveAll(m => m.Instrument == "qep.valkey.connected");
            }

            _listener.RecordObservableInstruments();
            lock (_measurements)
            {
                return Assert.Single(_measurements, m => m.Instrument == "qep.valkey.connected").Value;
            }
        }

        public void Dispose() => _listener.Dispose();

        private void Add(Instrument instrument, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            string? area = null;
            string? reason = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "area")
                {
                    area = tag.Value as string;
                }
                else if (tag.Key == "reason")
                {
                    reason = tag.Value as string;
                }
            }

            lock (_measurements)
            {
                _measurements.Add((instrument.Name, area, reason, value));
            }
        }
    }

    /// <summary>Guarda cada log ya formateado, con la excepción completa si la hay.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _entries = [];

        public IReadOnlyList<string> Entries
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose()
        {
        }

        private void Add(string entry)
        {
            lock (_entries)
            {
                _entries.Add(entry);
            }
        }

        private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Add($"{logLevel}: {formatter(state, exception)} {exception}");
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
