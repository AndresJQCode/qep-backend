using System.Diagnostics.Metrics;
using System.Threading.RateLimiting;
using Bootstrapper.Valkey;
using Bootstrapper.Valkey.RateLimiting;
using BuildingBlocks.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bootstrapper.UnitTests.Valkey;

/// <summary>
/// La ventana fija compartida de D-3 (spec 2026-11-13; P-4): <c>INCR</c> + <c>EXPIRE … NX</c> en un <c>MULTI</c>, sin Lua (V1
/// del plan). Cada prueba usa su propia política, así no comparten clave en el contenedor de la clase; el reloj es manual
/// y arranca en el inicio de una ventana de 60 s alineada a la época Unix (V6).
/// </summary>
public sealed class ValkeyRateLimiterTests(ValkeyPlainServer server) : IClassFixture<ValkeyPlainServer>
{
    private const string Partition = "203.0.113.7";

    [Fact]
    public async Task TwoLimitersOnTheSameKeyShareTheWindow()
    {
        var clock = RateLimitClock.AtWindowStart(60);
        await using var podA = await Node.StartAsync(PlainConnectionString());
        await using var podB = await Node.StartAsync(PlainConnectionString());
        var policy = NewPolicy();
        using var limiterA = podA.Limiter(policy, clock);
        using var limiterB = podB.Limiter(policy, clock);

        for (var i = 0; i < 5; i++)
        {
            Assert.True(await AcquireAsync(limiterA));
            Assert.True(await AcquireAsync(limiterB));
        }

        Assert.False(await AcquireAsync(limiterA));
        Assert.False(await AcquireAsync(limiterB));
        Assert.Equal(0, podA.Fallbacks.Count("rate_limiting") + podB.Fallbacks.Count("rate_limiting"));
    }

    [Fact]
    public async Task TheWindowResetsAtTheBoundary()
    {
        var clock = RateLimitClock.AtWindowStart(60);
        await using var pod = await Node.StartAsync(PlainConnectionString());
        using var limiter = pod.Limiter(NewPolicy(), clock);
        for (var i = 0; i < 10; i++)
        {
            Assert.True(await AcquireAsync(limiter));
        }

        Assert.False(await AcquireAsync(limiter));

        clock.Advance(TimeSpan.FromSeconds(60));

        Assert.True(await AcquireAsync(limiter));
    }

    [Fact]
    public async Task RetryAfterPointsToTheEndOfTheWindow()
    {
        var clock = RateLimitClock.AtWindowStart(60);
        clock.Advance(TimeSpan.FromSeconds(15));
        await using var pod = await Node.StartAsync(PlainConnectionString());
        using var limiter = pod.Limiter(NewPolicy(), clock);
        for (var i = 0; i < 10; i++)
        {
            Assert.True(await AcquireAsync(limiter));
        }

        using var rejected = await limiter.AcquireAsync(1, Ct);

        Assert.False(rejected.IsAcquired);
        Assert.True(rejected.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter));
        Assert.Equal(TimeSpan.FromSeconds(45), retryAfter);
    }

    [Fact]
    public async Task TheKeyExpiresAfterTheWindow()
    {
        var clock = RateLimitClock.AtWindowStart(60);
        await using var pod = await Node.StartAsync(PlainConnectionString());
        var policy = NewPolicy();
        using var limiter = pod.Limiter(policy, clock);

        Assert.True(await AcquireAsync(limiter));

        var key = ValkeyKeys.RateLimit(policy, Partition, clock.GetUtcNow().ToUnixTimeSeconds());
        var ttl = await pod.Database.KeyTimeToLiveAsync(key);
        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(61));
    }

    // Ningún camino de seguridad falla abierto: sin Valkey cuenta el limitador de memoria del pod.
    // Contenedor propio: detenerlo dejaría sin Valkey al resto de las pruebas de la clase.
    [Fact]
    public async Task WithValkeyDownTheLocalLimiterStillRejects()
    {
        await using var container = await ValkeyTestServer.StartAsync(withProductionAcl: false, Ct);
        var clock = RateLimitClock.AtWindowStart(60);
        await using var pod = await Node.StartAsync(ValkeyTestServer.ConnectionString(container, asQep: false));
        using var limiter = pod.Limiter(NewPolicy(), clock);
        Assert.True(await AcquireAsync(limiter));

        await container.StopAsync(Ct);

        for (var i = 0; i < 10; i++)
        {
            Assert.True(await AcquireAsync(limiter));
        }

        Assert.False(await AcquireAsync(limiter));
        Assert.True(pod.Fallbacks.Count("rate_limiting") >= 11);
    }

    // Review Focus 4: sin IdleDuration el limitador particionado nunca desaloja; con conteos en el respaldo, no debe.
    [Fact]
    public async Task AnIdleLimiterReportsItsIdleDuration()
    {
        var clock = RateLimitClock.AtWindowStart(60);
        await using var pod = await Node.StartAsync(PlainConnectionString());
        using var limiter = pod.Limiter(NewPolicy(), clock);
        Assert.True(await AcquireAsync(limiter));

        clock.Advance(TimeSpan.FromSeconds(15));

        Assert.NotNull(limiter.IdleDuration);
        Assert.True(limiter.IdleDuration >= TimeSpan.FromSeconds(15), $"IdleDuration = {limiter.IdleDuration}.");

        await using var down = await Node.StartAsync("127.0.0.1:1,connectTimeout=500");
        using var fallingBack = down.Limiter(NewPolicy(), clock);
        Assert.True(await AcquireAsync(fallingBack));
        clock.Advance(TimeSpan.FromSeconds(15));

        Assert.Null(fallingBack.IdleDuration);
    }

    // RateLimitingMiddleware llama primero AttemptAcquire y sólo con un rechazo pasa a AcquireAsync: si el camino síncrono
    // concediera desde memoria, ninguna política llegaría a Valkey. Se niega sin contar nada, para que decida el asíncrono.
    [Fact]
    public async Task TheSynchronousAttemptDefersToTheAsyncPath()
    {
        var clock = RateLimitClock.AtWindowStart(60);
        await using var pod = await Node.StartAsync(PlainConnectionString());
        var policy = NewPolicy();
        using var limiter = pod.Limiter(policy, clock);

        using (var attempt = limiter.AttemptAcquire(1))
        {
            Assert.False(attempt.IsAcquired);
            Assert.False(attempt.TryGetMetadata(MetadataName.RetryAfter, out _));
        }

        Assert.Null(limiter.GetStatistics());
        Assert.True(await AcquireAsync(limiter));
        var key = ValkeyKeys.RateLimit(policy, Partition, clock.GetUtcNow().ToUnixTimeSeconds());
        Assert.Equal(1, (long)await pod.Database.StringGetAsync(key));
    }

    // La ACL de producción quita @scripting (DP-V4): el contador no puede depender de EVAL.
    [Fact]
    public async Task TheRateLimitUsesNoScript()
    {
        await using var container = await ValkeyTestServer.StartAsync(withProductionAcl: true, Ct);
        var clock = RateLimitClock.AtWindowStart(60);
        await using var pod = await Node.StartAsync(ValkeyTestServer.ConnectionString(container, asQep: true));
        using var limiter = pod.Limiter(NewPolicy(), clock);

        for (var i = 0; i < 3; i++)
        {
            Assert.True(await AcquireAsync(limiter));
        }

        Assert.Equal(0, pod.Fallbacks.Count("rate_limiting"));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewPolicy() => "test-" + Guid.NewGuid().ToString("N");

    private static FixedWindowRateLimiterOptions TenPerMinute() => new()
    {
        PermitLimit = 10,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
    };

    private static async Task<bool> AcquireAsync(RateLimiter limiter)
    {
        using var lease = await limiter.AcquireAsync(1, Ct);
        return lease.IsAcquired;
    }

    private string PlainConnectionString() => ValkeyTestServer.ConnectionString(server.Container, asQep: false);

    /// <summary>Un pod: su multiplexer, su gateway y el contador de respaldos de su propio factory de métricas.</summary>
    private sealed class Node : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly ValkeyConnection _connection;
        private readonly ValkeyGateway _gateway;

        private Node(ServiceProvider provider, ValkeyConnection connection, ValkeyGateway gateway, FallbackCounter fallbacks)
        {
            _provider = provider;
            _connection = connection;
            _gateway = gateway;
            Fallbacks = fallbacks;
        }

        public FallbackCounter Fallbacks { get; }

        public StackExchange.Redis.IDatabase Database => _connection.Multiplexer!.GetDatabase();

        public static async Task<Node> StartAsync(string connectionString)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Valkey"] = connectionString })
                .Build();
            var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
            var factory = provider.GetRequiredService<IMeterFactory>();
            var options = Options.Create(new ValkeyOptions { Enabled = true, Areas = { RateLimiting = true } });

            var connection = new ValkeyConnection(configuration, options, NullLogger<ValkeyConnection>.Instance);
            var fallbacks = new FallbackCounter(factory);
            var gateway = new ValkeyGateway(
                connection, options, new ValkeyMetrics(factory, connection), NullLogger<ValkeyGateway>.Instance);
            await connection.StartAsync(Ct);
            return new Node(provider, connection, gateway, fallbacks);
        }

        public ValkeyFixedWindowRateLimiter Limiter(string policy, TimeProvider clock) =>
            new(_gateway, policy, Partition, TenPerMinute(), clock);

        public async ValueTask DisposeAsync()
        {
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

    /// <summary>
    /// Reloj manual de pared y de marcas: <c>GetElapsedTime</c> (el <c>IdleDuration</c>) se mide con <c>GetTimestamp</c>, así
    /// que también tiene que avanzar cuando la prueba lo mueve.
    /// </summary>
    private sealed class RateLimitClock : TimeProvider
    {
        private DateTimeOffset _now;

        private RateLimitClock(DateTimeOffset now) => _now = now;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        /// <summary>El inicio de la ventana de <paramref name="windowSeconds"/> en curso, alineada a la época Unix.</summary>
        public static RateLimitClock AtWindowStart(long windowSeconds)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return new RateLimitClock(DateTimeOffset.FromUnixTimeSeconds(now / windowSeconds * windowSeconds));
        }

        public override DateTimeOffset GetUtcNow() => _now;

        public override long GetTimestamp() => _now.UtcTicks;

        public void Advance(TimeSpan span) => _now += span;
    }
}
