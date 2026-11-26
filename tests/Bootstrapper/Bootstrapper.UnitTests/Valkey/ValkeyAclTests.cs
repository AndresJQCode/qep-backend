using Bootstrapper.Valkey;
using DotNet.Testcontainers.Containers;
using StackExchange.Redis;

namespace Bootstrapper.UnitTests.Valkey;

/// <summary>Un Valkey con la ACL de producción, uno para toda la clase.</summary>
public sealed class ValkeyAclServer : IAsyncLifetime
{
    internal IContainer Container { get; private set; } = null!;

    public async ValueTask InitializeAsync() =>
        Container = await ValkeyTestServer.StartAsync(withProductionAcl: true, TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => Container.DisposeAsync();
}

/// <summary>
/// Review Focus 1 del plan de Valkey: con <c>user default off</c> y <c>-@dangerous</c>, el <c>INFO</c>/<c>CONFIG</c> del
/// handshake, el canal <c>__Booksleeve_MasterChanged</c> o la clave de desempate pueden dejar el multiplexer
/// desconectado o lanzando <c>NOPERM</c>. Estas pruebas conectan con las mismas opciones que usa el backend.
/// </summary>
public sealed class ValkeyAclTests(ValkeyAclServer server) : IClassFixture<ValkeyAclServer>
{
    [Fact]
    public async Task TheMultiplexerWorksUnderTheProductionAcl()
    {
        await using var mux = await ConnectionMultiplexer.ConnectAsync(
            ValkeyConnection.BuildOptions(ValkeyTestServer.ConnectionString(server.Container, asQep: true)));
        Assert.True(mux.IsConnected);
        var db = mux.GetDatabase();

        Assert.Equal(1, await db.StringIncrementAsync("qep:probe"));
        Assert.True(await db.KeyExpireAsync("qep:probe", TimeSpan.FromSeconds(61), ExpireWhen.HasNoExpiry));
        Assert.False(await db.KeyExpireAsync("qep:probe", TimeSpan.FromSeconds(61), ExpireWhen.HasNoExpiry));

        // La primitiva del limitador (V1): INCR + EXPIRE NX en un MULTI, sin Lua.
        var transaction = db.CreateTransaction();
        _ = transaction.StringIncrementAsync("qep:rl:probe");
        _ = transaction.KeyExpireAsync("qep:rl:probe", TimeSpan.FromSeconds(61), ExpireWhen.HasNoExpiry);
        Assert.True(await transaction.ExecuteAsync());

        Assert.True(await db.SortedSetAddAsync("qep:presence:probe", "m", 1));

        await mux.GetSubscriber().PublishAsync(RedisChannel.Literal("qep:wake:probe"), "");
    }

    // ValkeyACLDenied (2026-11-17) disparaba todo el tiempo: el cliente mandaba INFO y CONFIG al conectar y en cada chequeo
    // de configuración (ConfigCheckSeconds, 60 s), y -@dangerous los rechaza. El ruido tapa la señal de seguridad: con las
    // opciones del backend y la ACL de producción (con +info), conectar, usar y reconfigurar no deja nada en ACL LOG.
    // Sin +info la prueba vuelve a fallar, con un SET sobre una clave binaria: es la sonda de réplica del cliente.
    [Fact]
    public async Task TheClientSendsNothingTheAclDenies()
    {
        await using var auditor = await ConnectionMultiplexer.ConnectAsync(
            ValkeyTestServer.AuditorConnectionString(server.Container));
        var auditorDb = auditor.GetDatabase();
        await auditorDb.ExecuteAsync("ACL", "LOG", "RESET");

        await using (var mux = await ConnectionMultiplexer.ConnectAsync(
            ValkeyConnection.BuildOptions(ValkeyTestServer.ConnectionString(server.Container, asQep: true))))
        {
            var db = mux.GetDatabase();
            await db.StringIncrementAsync("qep:noise:probe");
            await db.KeyExpireAsync("qep:noise:probe", TimeSpan.FromSeconds(61), ExpireWhen.HasNoExpiry);
            await mux.GetSubscriber().PublishAsync(RedisChannel.Literal("qep:wake:noise"), "");

            // Lo mismo que hace el chequeo periódico de configuración.
            Assert.True(await mux.ConfigureAsync());
            Assert.True(mux.IsConnected);
        }

        var entries = (RedisResult[])(await auditorDb.ExecuteAsync("ACL", "LOG"))!;
        var denied = entries
            .Select(entry => entry.ToDictionary())
            .Select(entry => $"{entry["username"]}:{entry["reason"]}:{entry["object"]} [{entry["client-info"]}]")
            .ToList();
        Assert.True(denied.Count == 0, "ACL LOG: " + string.Join(", ", denied));
    }

    // Prueba que la anterior no pasa por vacía: la ACL de verdad está cargada.
    [Fact]
    public async Task TheAclIsReallyLoaded()
    {
        await using var mux = await ConnectionMultiplexer.ConnectAsync(
            ValkeyConnection.BuildOptions(ValkeyTestServer.ConnectionString(server.Container, asQep: true)));
        var db = mux.GetDatabase();

        var outsideThePrefix = await Assert.ThrowsAsync<RedisServerException>(() => db.StringSetAsync("other:x", "1"));
        Assert.Contains("NOPERM", outsideThePrefix.Message, StringComparison.Ordinal);

        var script = await Assert.ThrowsAsync<RedisServerException>(() => db.ScriptEvaluateAsync("return 1"));
        Assert.Contains("NOPERM", script.Message, StringComparison.Ordinal);
    }
}
