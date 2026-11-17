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
