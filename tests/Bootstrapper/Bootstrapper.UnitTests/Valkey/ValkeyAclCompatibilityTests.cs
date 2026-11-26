using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using Bootstrapper.Valkey;
using Bootstrapper.Valkey.Permissions;
using Bootstrapper.Valkey.Sessions;
using BuildingBlocks.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Modules.Authorization.Application;
using Modules.Buyers.Application;
using Modules.Identity.Application;
using Modules.Tenancy.Application;
using StackExchange.Redis;

namespace Bootstrapper.UnitTests.Valkey;

/// <summary>
/// Cada área de Valkey funciona bajo la ACL de producción (spec 2026-11-13, DP-V4; Tarea 8 del plan de optimizaciones):
/// <c>user default off</c> y <c>qep ~qep:* &amp;qep:* +@all -@dangerous -@scripting</c>. Pruebas de caracterización con los
/// adaptadores reales: el gateway traga un <c>NOPERM</c> como respaldo, así que cada prueba exige el efecto en Valkey <b>y</b>
/// cero respaldos del área. Si una falla es un hallazgo: no se arregla relajando la ACL.
/// </summary>
/// <remarks>
/// El presupuesto de envío (D-6) se quedó en Postgres por decisión del owner (2026-11-13): no hay transacción optimista que
/// probar acá. El contenedor es uno para la clase (<see cref="ValkeyAclServer"/>, el de <see cref="ValkeyAclTests"/>); cada
/// prueba usa claves propias.
/// </remarks>
public sealed class ValkeyAclCompatibilityTests(ValkeyAclServer server) : IClassFixture<ValkeyAclServer>
{
    // Sesiones (D-4): SET EX, MGET de entrada y marca, y la revocación en lote (SET de la marca + DEL), de Identity y de comprador.
    [Fact]
    public async Task SessionCacheWorksUnderTheProductionAcl()
    {
        await using var harness = await Harness.StartAsync(ConnectionString());
        var identityCache = new ValkeySessionCache(
            harness.Gateway, TimeProvider.System, NullLogger<ValkeySessionCache>.Instance);
        var buyerCache = new ValkeyBuyerSessionCache(
            harness.Gateway, TimeProvider.System, NullLogger<ValkeyBuyerSessionCache>.Instance);
        var now = TimeProvider.System.GetUtcNow();
        var identityHash = NewHash();
        var identitySession = new CachedSession(Guid.CreateVersion7(), Guid.CreateVersion7(), now, now.AddDays(7));
        var buyerHash = NewHash();
        var buyerSession = new CachedBuyerSession(
            Guid.NewGuid(), Guid.NewGuid(), "acme.lulocrm.com", now.AddDays(-1), now, now.AddDays(30));

        await identityCache.SetAsync(identityHash, identitySession, Ct);
        await buyerCache.SetAsync(buyerHash, buyerSession, Ct);
        Assert.Equal(identitySession, await identityCache.GetAsync(identityHash, Ct));
        Assert.Equal(buyerSession, await buyerCache.GetAsync(buyerHash, Ct));
        await AssertTtlAsync(harness, $"qep:sess:{identityHash}", SessionCacheTtl.MaxEntry);
        await AssertTtlAsync(harness, $"qep:sess:buyer:{buyerHash}", SessionCacheTtl.MaxEntry);

        await identityCache.RemoveAsync([new RevokedSession(identitySession.SessionId, identityHash)], Ct);
        await buyerCache.RemoveAsync([new RevokedBuyerSession(buyerSession.SessionId, buyerHash)], Ct);
        Assert.Null(await identityCache.GetAsync(identityHash, Ct));
        Assert.Null(await buyerCache.GetAsync(buyerHash, Ct));
        await AssertTtlAsync(harness, $"qep:sess:rev:{identityHash}", SessionCacheTtl.RevocationMarker);
        await AssertTtlAsync(harness, $"qep:sess:buyer:rev:{buyerHash}", SessionCacheTtl.RevocationMarker);

        Assert.Equal(0, harness.Fallbacks.Count("sessions"));
    }

    // Permisos (D-5): MGET de entrada y versión, SET NX de la semilla, SET EX de la entrada, y el bump (SET NX + INCR + EXPIRE en
    // un MULTI). La segunda resolución sale de la caché; la tercera, después del bump, vuelve a Postgres.
    [Fact]
    public async Task TenantVersionWorksUnderTheProductionAcl()
    {
        await using var harness = await Harness.StartAsync(ConnectionString());
        var memberships = new CountingMembershipDirectory();
        var authorization = new CachedAuthorizationService(
            new AuthorizationService(memberships, null!, null!, new ModuleEntitlementMask([]), null!),
            harness.Gateway,
            TimeProvider.System);
        var version = new ValkeyTenantAccessVersion(
            harness.Gateway, TimeProvider.System, NullLogger<ValkeyTenantAccessVersion>.Instance);
        var userId = Guid.CreateVersion7();
        var tenantId = Guid.CreateVersion7();
        var versionKey = $"qep:tenantver:{tenantId:D}";

        Assert.Null(await authorization.ResolvePermissionsAsync(userId, tenantId, Ct));
        var seeded = (long)await harness.Database.StringGetAsync(versionKey);
        Assert.Null(await authorization.ResolvePermissionsAsync(userId, tenantId, Ct));
        Assert.Equal(1, memberships.Calls);

        await version.BumpAsync(tenantId, Ct);
        Assert.Equal(seeded + 1, (long)await harness.Database.StringGetAsync(versionKey));
        var ttl = await harness.Database.KeyTimeToLiveAsync(versionKey);
        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value, TimeSpan.FromDays(29), TimeSpan.FromDays(30));

        Assert.Null(await authorization.ResolvePermissionsAsync(userId, tenantId, Ct));
        Assert.Equal(2, memberships.Calls);

        Assert.Equal(0, harness.Fallbacks.Count("permissions"));
    }

    // Timbres (D-7): SUBSCRIBE y PUBLISH en qep:wake:{cola}. El PUBLISH es FireAndForget, así que un NOPERM no lanza: lo que
    // prueba que pasó es que el oyente despierta antes de su red de seguridad.
    [Fact]
    public async Task DoorbellsWorkUnderTheProductionAcl()
    {
        await using var harness = await Harness.StartAsync(ConnectionString());
        var doorbell = new ValkeyQueueDoorbell(harness.Gateway);
        using var listener = doorbell.Listen(DoorbellQueues.Outbox);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!doorbell.IsSubscribed(DoorbellQueues.Outbox) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, Ct);
        }

        Assert.True(
            doorbell.IsSubscribed(DoorbellQueues.Outbox),
            "No se suscribió a qep:wake:outbox: revisa SUBSCRIBE o el permiso de canales de la ACL (&qep:*).");
        var stopwatch = Stopwatch.StartNew();
        var waiting = listener.WaitAsync(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), Ct);
        await Task.Delay(100, Ct);
        doorbell.Ring(DoorbellQueues.Outbox);

        Assert.True(await waiting);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Tardó {stopwatch.Elapsed}.");
        Assert.Equal(0, harness.Fallbacks.Count("doorbells"));
    }

    // Compuerta del toque (D-8, SET NX EX 60) y líder del muestreador (D-7/P14, SET NX PX 15000, GET y EXPIRE para renovar).
    // Los dos dejan pasar si Valkey falla, así que el «false» del segundo intento es lo que prueba que el comando corrió.
    [Fact]
    public async Task TouchGateAndLeaderWorkUnderTheProductionAcl()
    {
        await using var harness = await Harness.StartAsync(ConnectionString());
        var gate = new ValkeyApiKeyTouchGate(harness.Gateway);
        var keyId = Guid.CreateVersion7();

        Assert.True(await gate.ShouldTouchAsync(keyId, Ct));
        Assert.False(await gate.ShouldTouchAsync(keyId, Ct));
        var touchTtl = await harness.Database.KeyTimeToLiveAsync($"qep:touch:apikey:{keyId:D}");
        Assert.NotNull(touchTtl);
        Assert.InRange(touchTtl.Value, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60));

        // La clave del líder es fija: se limpia antes por si otra prueba de la clase la dejó (claves explícitas, nunca patrón).
        await harness.Database.KeyDeleteAsync(QueueSamplerLeadership.Key);
        var leader = new QueueSamplerLeadership(harness.Gateway);
        var follower = new QueueSamplerLeadership(harness.Gateway);

        Assert.True(await leader.TryLeadAsync(Ct));
        Assert.False(await follower.TryLeadAsync(Ct));
        Assert.True(await leader.TryLeadAsync(Ct));
        var leaderTtl = await harness.Database.KeyTimeToLiveAsync(QueueSamplerLeadership.Key);
        Assert.NotNull(leaderTtl);
        Assert.InRange(leaderTtl.Value, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(15));

        Assert.Equal(0, harness.Fallbacks.Count("api_key_touch"));
        Assert.Equal(0, harness.Fallbacks.Count("doorbells"));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>La clave existe y su TTL es positivo y no pasa del tope.</summary>
    private static async Task AssertTtlAsync(Harness harness, string key, TimeSpan max)
    {
        var ttl = await harness.Database.KeyTimeToLiveAsync(key);
        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value, TimeSpan.FromMilliseconds(1), max);
    }

    private string ConnectionString() => ValkeyTestServer.ConnectionString(server.Container, asQep: true);

    private static string NewHash() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    /// <summary>Sin membresía activa: <see cref="AuthorizationService"/> devuelve <c>null</c> sin tocar sus otras dependencias.</summary>
    private sealed class CountingMembershipDirectory : IMembershipDirectory
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<IReadOnlyCollection<string>?> FindActiveRolesAsync(
            Guid userId, Guid tenantId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult<IReadOnlyCollection<string>?>(null);
        }

        public Task<Guid?> FindActiveMembershipIdAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Guid>> ListMembershipIdsByUserAsync(Guid userId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    /// <summary>Gateway con las áreas de Bloque A y B prendidas, conectado como el usuario <c>qep</c>.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly ValkeyConnection _connection;

        private Harness(
            ServiceProvider provider,
            ValkeyConnection connection,
            ValkeyGateway gateway,
            ValkeySessionCacheTests.FallbackCounter fallbacks)
        {
            _provider = provider;
            _connection = connection;
            Gateway = gateway;
            Fallbacks = fallbacks;
        }

        public ValkeyGateway Gateway { get; }

        public ValkeySessionCacheTests.FallbackCounter Fallbacks { get; }

        public IDatabase Database => _connection.Multiplexer!.GetDatabase();

        public static async Task<Harness> StartAsync(string connectionString)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Valkey"] = connectionString })
                .Build();
            var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
            var factory = provider.GetRequiredService<IMeterFactory>();
            var options = Options.Create(new ValkeyOptions
            {
                Enabled = true,
                // Más holgado que los 100 ms de producción: la prueba mide permisos, no latencia del contenedor.
                OperationTimeoutMs = 2000,
                Areas = { Sessions = true, Permissions = true, Doorbells = true, ApiKeyTouch = true },
            });
            var connection = new ValkeyConnection(configuration, options, NullLogger<ValkeyConnection>.Instance);
            var fallbacks = new ValkeySessionCacheTests.FallbackCounter(factory);
            var gateway = new ValkeyGateway(
                connection, options, new ValkeyMetrics(factory, connection), NullLogger<ValkeyGateway>.Instance);
            await connection.StartAsync(Ct);
            Assert.True(gateway.IsConnected);
            return new Harness(provider, connection, gateway, fallbacks);
        }

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync();
            Fallbacks.Dispose();
            await _provider.DisposeAsync();
        }
    }
}
