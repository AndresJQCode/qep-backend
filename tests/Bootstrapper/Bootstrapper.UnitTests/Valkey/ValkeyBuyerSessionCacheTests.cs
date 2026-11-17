using System.Security.Cryptography;
using Bootstrapper.Valkey;
using Bootstrapper.Valkey.Sessions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Modules.Buyers.Application;
using Modules.Buyers.Domain;
using StackExchange.Redis;

namespace Bootstrapper.UnitTests.Valkey;

/// <summary>
/// La caché de sesión del comprador (spec 2026-11-13, D-4; plan P2): mismo patrón que <see cref="ValkeySessionCacheTests"/>,
/// con entrada <c>qep:sess:buyer:{hash}</c>, marca <c>qep:sess:buyer:rev:{hash}</c> y un TTL que sale de
/// <c>min(ExpiresAt, LastSeenAt + IdleTimeout)</c>, nunca de más de 15 minutos.
/// </summary>
public sealed class ValkeyBuyerSessionCacheTests(ValkeyPlainServer server) : IClassFixture<ValkeyPlainServer>
{
    [Fact]
    public async Task ASetEntryIsReadBack()
    {
        await using var harness = await Harness.StartAsync(ConnectionString());
        var hash = NewHash();
        var session = NewSession(lastSeenAt: Now, expiresAt: Now.AddDays(30));

        await harness.Cache.SetAsync(hash, session, Ct);

        Assert.Equal(session, await harness.Cache.GetAsync(hash, Ct));
    }

    [Fact]
    public async Task TheTtlNeverExceedsFifteenMinutes()
    {
        await using var harness = await Harness.StartAsync(ConnectionString());
        var hash = NewHash();

        await harness.Cache.SetAsync(hash, NewSession(lastSeenAt: Now, expiresAt: Now.AddDays(30)), Ct);

        var ttl = await harness.Database.KeyTimeToLiveAsync($"qep:sess:buyer:{hash}");
        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value, TimeSpan.FromMinutes(14), TimeSpan.FromSeconds(900));
    }

    [Fact]
    public async Task TheTtlFollowsAnEarlierExpiry()
    {
        await using var harness = await Harness.StartAsync(ConnectionString());
        var hash = NewHash();

        await harness.Cache.SetAsync(hash, NewSession(lastSeenAt: Now, expiresAt: Now.AddMinutes(2)), Ct);

        var ttl = await harness.Database.KeyTimeToLiveAsync($"qep:sess:buyer:{hash}");
        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(120));
    }

    // La inactividad también acota: visto hace 7 días menos un minuto, le queda un minuto aunque la vida absoluta siga.
    [Fact]
    public async Task TheTtlFollowsTheIdleTimeout()
    {
        await using var harness = await Harness.StartAsync(ConnectionString());
        var hash = NewHash();
        var lastSeenAt = Now - BuyerSession.IdleTimeout + TimeSpan.FromMinutes(1);

        await harness.Cache.SetAsync(hash, NewSession(lastSeenAt, expiresAt: Now.AddDays(20)), Ct);

        var ttl = await harness.Database.KeyTimeToLiveAsync($"qep:sess:buyer:{hash}");
        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60));
    }

    // Review Focus 1, del lado del comprador: el lector atrasado escribe después del segundo DEL y la marca gana.
    [Fact]
    public async Task ARepopulateAfterTheRevokeIsIgnored()
    {
        await using var harness = await Harness.StartAsync(ConnectionString());
        var hash = NewHash();
        var session = NewSession(lastSeenAt: Now, expiresAt: Now.AddDays(30));
        await harness.Cache.SetAsync(hash, session, Ct);

        await harness.Cache.RemoveAsync([new RevokedBuyerSession(session.SessionId, hash)], Ct);
        await harness.Cache.SetAsync(hash, session, Ct);

        Assert.Null(await harness.Cache.GetAsync(hash, Ct));
        var markerTtl = await harness.Database.KeyTimeToLiveAsync($"qep:sess:buyer:rev:{hash}");
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
        await harness.Cache.SetAsync(hash, NewSession(lastSeenAt: Now, expiresAt: Now.AddDays(30)), Ct);
        await container.StopAsync(Ct);
        var before = harness.Fallbacks.Count("sessions");

        Assert.Null(await harness.Cache.GetAsync(hash, Ct));
        Assert.Equal(before + 1, harness.Fallbacks.Count("sessions"));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTimeOffset Now => TimeProvider.System.GetUtcNow();

    private string ConnectionString() => ValkeyTestServer.ConnectionString(server.Container, asQep: false);

    private static string NewHash() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    private static CachedBuyerSession NewSession(DateTimeOffset lastSeenAt, DateTimeOffset expiresAt) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "acme.lulocrm.com", lastSeenAt.AddDays(-1), lastSeenAt, expiresAt);

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly ValkeyConnection _connection;

        private Harness(
            ServiceProvider provider,
            ValkeyConnection connection,
            ValkeyBuyerSessionCache cache,
            ValkeySessionCacheTests.FallbackCounter fallbacks)
        {
            _provider = provider;
            _connection = connection;
            Cache = cache;
            Fallbacks = fallbacks;
        }

        public ValkeyBuyerSessionCache Cache { get; }

        public ValkeySessionCacheTests.FallbackCounter Fallbacks { get; }

        public IDatabase Database => _connection.Multiplexer!.GetDatabase();

        public static async Task<Harness> StartAsync(string connectionString)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Valkey"] = connectionString })
                .Build();
            var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
            var factory = provider.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>();
            var options = Options.Create(new ValkeyOptions { Enabled = true, Areas = { Sessions = true } });
            var connection = new ValkeyConnection(configuration, options, NullLogger<ValkeyConnection>.Instance);
            var fallbacks = new ValkeySessionCacheTests.FallbackCounter(factory);
            var gateway = new ValkeyGateway(
                connection, options, new ValkeyMetrics(factory, connection), NullLogger<ValkeyGateway>.Instance);
            await connection.StartAsync(Ct);
            var cache = new ValkeyBuyerSessionCache(gateway, TimeProvider.System, NullLogger<ValkeyBuyerSessionCache>.Instance);
            return new Harness(provider, connection, cache, fallbacks);
        }

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync();
            Fallbacks.Dispose();
            await _provider.DisposeAsync();
        }
    }
}
