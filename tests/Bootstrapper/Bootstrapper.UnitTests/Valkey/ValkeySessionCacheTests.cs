using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using Bootstrapper.Valkey;
using Bootstrapper.Valkey.Sessions;
using BuildingBlocks.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Modules.Identity.Application;
using StackExchange.Redis;

namespace Bootstrapper.UnitTests.Valkey;

/// <summary>
/// La caché de sesión de Identity (spec 2026-11-13, D-4; plan P2): la entrada vive en <c>qep:sess:{hash}</c> con TTL de a lo sumo
/// 15 minutos, y revocar deja una marca <c>qep:sess:rev:{hash}</c> que gana sobre cualquier entrada escrita después. Contenedor
/// real, sin doble del multiplexer; un hash nuevo por prueba, así las pruebas no se cruzan en el contenedor de la clase.
/// </summary>
public sealed class ValkeySessionCacheTests(ValkeyPlainServer server) : IClassFixture<ValkeyPlainServer>
{
    [Fact]
    public async Task ASetEntryIsReadBack()
    {
        await using var harness = await Harness.StartAsync(ConnectionString());
        var hash = NewHash();
        var session = NewSession(Harness.Now + TimeSpan.FromDays(7));

        await harness.Cache.SetAsync(hash, session, Ct);

        Assert.Equal(session, await harness.Cache.GetAsync(hash, Ct));
    }

    [Fact]
    public async Task TheTtlNeverExceedsFifteenMinutes()
    {
        await using var harness = await Harness.StartAsync(ConnectionString());
        var hash = NewHash();

        await harness.Cache.SetAsync(hash, NewSession(Harness.Now + TimeSpan.FromDays(30)), Ct);

        var ttl = await harness.Database.KeyTimeToLiveAsync($"qep:sess:{hash}");
        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value, TimeSpan.FromMinutes(14), TimeSpan.FromSeconds(900));
    }

    [Fact]
    public async Task TheTtlFollowsAnEarlierValidUntil()
    {
        await using var harness = await Harness.StartAsync(ConnectionString());
        var hash = NewHash();

        await harness.Cache.SetAsync(hash, NewSession(Harness.Now + TimeSpan.FromMinutes(2)), Ct);

        var ttl = await harness.Database.KeyTimeToLiveAsync($"qep:sess:{hash}");
        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(120));
    }

    [Fact]
    public async Task RemoveDeletesTheEntry()
    {
        await using var harness = await Harness.StartAsync(ConnectionString());
        var hash = NewHash();
        var session = NewSession(Harness.Now + TimeSpan.FromDays(7));
        await harness.Cache.SetAsync(hash, session, Ct);

        await harness.Cache.RemoveAsync([new RevokedSession(session.SessionId, hash)], Ct);

        Assert.Null(await harness.Cache.GetAsync(hash, Ct));
        Assert.False(await harness.Database.KeyExistsAsync($"qep:sess:{hash}"));
    }

    // Review Focus 1: el lector atrasado leyó Postgres antes del commit del logout y escribe después del segundo DEL.
    [Fact]
    public async Task ARepopulateAfterTheRevokeIsIgnored()
    {
        await using var harness = await Harness.StartAsync(ConnectionString());
        var hash = NewHash();
        var session = NewSession(Harness.Now + TimeSpan.FromDays(7));
        await harness.Cache.SetAsync(hash, session, Ct);

        await harness.Cache.RemoveAsync([new RevokedSession(session.SessionId, hash)], Ct);
        await harness.Cache.SetAsync(hash, session, Ct);

        Assert.Null(await harness.Cache.GetAsync(hash, Ct));
        var markerTtl = await harness.Database.KeyTimeToLiveAsync($"qep:sess:rev:{hash}");
        Assert.NotNull(markerTtl);
        Assert.InRange(markerTtl.Value, TimeSpan.FromMinutes(19), TimeSpan.FromMinutes(20));
    }

    // Contenedor propio: detenerlo dejaría sin Valkey al resto de las pruebas de la clase.
    [Fact]
    public async Task WithValkeyDownGetReturnsNullAndCountsAFallback()
    {
        await using var container = await ValkeyTestServer.StartAsync(withProductionAcl: false, Ct);
        await using var harness = await Harness.StartAsync(ValkeyTestServer.ConnectionString(container, asQep: false));
        var hash = NewHash();
        await harness.Cache.SetAsync(hash, NewSession(Harness.Now + TimeSpan.FromDays(7)), Ct);
        await container.StopAsync(Ct);
        var before = harness.Fallbacks.Count("sessions");

        Assert.Null(await harness.Cache.GetAsync(hash, Ct));
        Assert.Equal(before + 1, harness.Fallbacks.Count("sessions"));
    }

    [Fact]
    public async Task TheKeyIsTheHashNeverTheToken()
    {
        await using var container = await ValkeyTestServer.StartAsync(withProductionAcl: false, Ct);
        await using var harness = await Harness.StartAsync(ValkeyTestServer.ConnectionString(container, asQep: false));
        var rawToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=');
        var hash = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawToken)));
        var session = NewSession(Harness.Now + TimeSpan.FromDays(7));

        await harness.Cache.SetAsync(hash, session, Ct);
        await harness.Cache.RemoveAsync([new RevokedSession(session.SessionId, hash)], Ct);

        // Contenedor propio: las únicas claves son las de esta prueba.
        var keys = harness.Server.Keys(pattern: "*").Select(key => (string)key!).ToList();
        Assert.Contains($"qep:sess:rev:{hash}", keys);
        Assert.All(keys, key => Assert.DoesNotContain(rawToken, key, StringComparison.Ordinal));
        Assert.All(keys, key => Assert.StartsWith("qep:", key, StringComparison.Ordinal));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string ConnectionString() => ValkeyTestServer.ConnectionString(server.Container, asQep: false);

    private static string NewHash() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    private static CachedSession NewSession(DateTimeOffset validUntil) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), DateTimeOffset.UtcNow, validUntil);

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly ValkeyConnection _connection;

        private Harness(ServiceProvider provider, ValkeyConnection connection, ValkeySessionCache cache, FallbackCounter fallbacks)
        {
            _provider = provider;
            _connection = connection;
            Cache = cache;
            Fallbacks = fallbacks;
        }

        public ValkeySessionCache Cache { get; }

        public FallbackCounter Fallbacks { get; }

        public static DateTimeOffset Now => TimeProvider.System.GetUtcNow();

        public IDatabase Database => _connection.Multiplexer!.GetDatabase();

        public IServer Server => _connection.Multiplexer!.GetServers()[0];

        public static async Task<Harness> StartAsync(string connectionString)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Valkey"] = connectionString })
                .Build();
            var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
            var factory = provider.GetRequiredService<IMeterFactory>();
            var options = Options.Create(new ValkeyOptions { Enabled = true, Areas = { Sessions = true } });
            var connection = new ValkeyConnection(configuration, options, NullLogger<ValkeyConnection>.Instance);
            var fallbacks = new FallbackCounter(factory);
            var gateway = new ValkeyGateway(
                connection, options, new ValkeyMetrics(factory, connection), NullLogger<ValkeyGateway>.Instance);
            await connection.StartAsync(Ct);
            var cache = new ValkeySessionCache(gateway, TimeProvider.System, NullLogger<ValkeySessionCache>.Instance);
            return new Harness(provider, connection, cache, fallbacks);
        }

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync();
            Fallbacks.Dispose();
            await _provider.DisposeAsync();
        }
    }

    /// <summary>Cuenta <c>qep.valkey.fallbacks</c> por área, sólo del factory de este harness.</summary>
    internal sealed class FallbackCounter : IDisposable
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
