using System.Data.Common;
using System.Net;
using System.Text.Json.Nodes;
using DotNet.Testcontainers.Containers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Modules.Buyers.Application;
using Modules.Buyers.Infrastructure.Persistence;
using StackExchange.Redis;
using static Modules.Buyers.IntegrationTests.BuyersApiHarness;

namespace Modules.Buyers.IntegrationTests;

/// <summary>
/// La caché de sesión del comprador en Valkey de punta a punta (spec 2026-11-13, D-4): con <c>Valkey:Areas:Sessions</c>
/// prendido, un request con la sesión ya resuelta no lee <c>buyers.sessions</c>, y todo camino que revoca o borra una sesión
/// saca su entrada (<c>qep:sess:buyer:{hash}</c>). Las que paran Valkey usan un contenedor propio.
/// </summary>
public sealed class CachedBuyerSessionTests(CachedBuyerSessionTests.SharedValkey valkey)
    : IClassFixture<CachedBuyerSessionTests.SharedValkey>
{
    private const string Email = "ana@correo.com";
    private const string Password = "una-contrasena-larga";
    private const string NewPassword = "otra-contrasena-nueva";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheSecondRequestDoesNotReadTheDatabase()
    {
        var counter = new SessionCommandCounter();
        await using var api = await OpenWithValkeyAsync(valkey.ConnectionString, counter);
        await api.RegisterVerifiedAsync();
        var token = await LoginTokenAsync(api);

        Assert.Equal(HttpStatusCode.OK, (await api.SendAsync(HttpMethod.Get, "/session", token)).StatusCode);
        Assert.True(counter.Count > 0, "El primer request tiene que leer la sesión de Postgres.");
        counter.Reset();

        Assert.Equal(HttpStatusCode.OK, (await api.SendAsync(HttpMethod.Get, "/session", token)).StatusCode);
        Assert.Equal(0, counter.Count);
    }

    // Review Focus 2: con la sesión en caché, el UPDATE del logout tiene que salir igual.
    [Fact]
    public async Task LogoutWithACachedSessionRevokesTheRow()
    {
        await using var api = await OpenWithValkeyAsync(valkey.ConnectionString);
        await api.RegisterVerifiedAsync();
        var token = await LoginTokenAsync(api);
        Assert.Equal(HttpStatusCode.OK, (await api.SendAsync(HttpMethod.Get, "/session", token)).StatusCode);
        Assert.True(await valkey.Database.KeyExistsAsync(EntryKey(token)));

        Assert.Equal(HttpStatusCode.NoContent, (await api.SendAsync(HttpMethod.Delete, "/session", token)).StatusCode);

        Assert.Equal(
            1,
            await api.ScalarAsync<long>(
                "SELECT count(*) FROM buyers.sessions WHERE token_hash = $1 AND revoked_at IS NOT NULL",
                BuyerTokenCodec.Hash(token)));
        Assert.False(await valkey.Database.KeyExistsAsync(EntryKey(token)));
        await AssertSessionInvalidAsync(await api.SendAsync(HttpMethod.Get, "/session", token));
    }

    [Fact]
    public async Task PasswordRecoveryRemovesEveryEntry()
    {
        await using var api = await OpenWithValkeyAsync(valkey.ConnectionString);
        await api.RegisterVerifiedAsync();
        var first = await LoginTokenAsync(api);
        var second = await LoginTokenAsync(api);
        await WarmAsync(api, first, second);

        Assert.Equal(
            HttpStatusCode.Accepted,
            (await api.PostAsync("/password/forgot", new { email = Email, turnstileToken = "token-ok" })).StatusCode);
        var resetToken = (await api.OutboxPayloadsAsync("buyers.password-reset-requested.v1"))
            .Last(payload => (string?)payload["email"] == Email)["token"]!.GetValue<string>();
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await api.PostAsync("/password/reset", new { token = resetToken, newPassword = NewPassword })).StatusCode);

        foreach (var token in new[] { first, second })
        {
            Assert.False(await valkey.Database.KeyExistsAsync(EntryKey(token)));
            await AssertSessionInvalidAsync(await api.SendAsync(HttpMethod.Get, "/session", token));
        }
    }

    [Fact]
    public async Task ChangePasswordRemovesTheOtherEntriesAndKeepsTheCurrent()
    {
        await using var api = await OpenWithValkeyAsync(valkey.ConnectionString);
        await api.RegisterVerifiedAsync();
        var current = await LoginTokenAsync(api);
        var other = await LoginTokenAsync(api);
        await WarmAsync(api, current, other);

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await api.PostAsync("/password", new { currentPassword = Password, newPassword = NewPassword }, session: current))
                .StatusCode);

        Assert.False(await valkey.Database.KeyExistsAsync(EntryKey(other)));
        await AssertSessionInvalidAsync(await api.SendAsync(HttpMethod.Get, "/session", other));
        Assert.Equal(HttpStatusCode.OK, (await api.SendAsync(HttpMethod.Get, "/session", current)).StatusCode);
    }

    [Fact]
    public async Task DeletingTheAccountRemovesItsEntries()
    {
        await using var api = await OpenWithValkeyAsync(valkey.ConnectionString);
        await api.RegisterVerifiedAsync();
        var current = await LoginTokenAsync(api);
        var other = await LoginTokenAsync(api);
        await WarmAsync(api, current, other);

        (await api.PostAsync("/account/delete", new { password = Password }, session: current)).EnsureSuccessStatusCode();

        foreach (var token in new[] { current, other })
        {
            Assert.False(await valkey.Database.KeyExistsAsync(EntryKey(token)));
            await AssertSessionInvalidAsync(await api.SendAsync(HttpMethod.Get, "/session", token));
        }
    }

    [Fact]
    public async Task ARevokedBuyerSessionIsNotServedWhileValkeyIsDown()
    {
        await using var ownValkey = await ValkeyTestServer.StartAsync(Ct);
        await using var api = await OpenWithValkeyAsync(ValkeyTestServer.ConnectionString(ownValkey));
        await api.RegisterVerifiedAsync();
        var token = await LoginTokenAsync(api);
        Assert.Equal(HttpStatusCode.OK, (await api.SendAsync(HttpMethod.Get, "/session", token)).StatusCode);
        using (var direct = await ConnectionMultiplexer.ConnectAsync(ValkeyTestServer.ConnectionString(ownValkey)))
        {
            Assert.True(await direct.GetDatabase().KeyExistsAsync(EntryKey(token)));
        }

        await ownValkey.StopAsync(Ct);
        await api.ScalarAsync<long>(
            "UPDATE buyers.sessions SET revoked_at = now(), revoked_reason = 'test' WHERE token_hash = $1",
            BuyerTokenCodec.Hash(token));

        await AssertSessionInvalidAsync(await api.SendAsync(HttpMethod.Get, "/session", token));
    }

    private static Task<BuyersApi> OpenWithValkeyAsync(string valkeyConnectionString, SessionCommandCounter? counter = null) =>
        OpenAsync(
            services =>
            {
                if (counter is not null)
                {
                    services.ConfigureDbContext<BuyersDbContext>(options => options.AddInterceptors(counter));
                }
            },
            settings: new Dictionary<string, string?>
            {
                ["Valkey:Enabled"] = "true",
                ["Valkey:Areas:Sessions"] = "true",
                ["ConnectionStrings:Valkey"] = valkeyConnectionString,
            });

    private static async Task<string> LoginTokenAsync(BuyersApi api)
    {
        var response = await api.PostAsync("/session", new { email = Email, password = Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (string)JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["sessionToken"]!;
    }

    // Deja la entrada de cada sesión en la caché, y comprueba que está.
    private async Task WarmAsync(BuyersApi api, params string[] tokens)
    {
        foreach (var token in tokens)
        {
            Assert.Equal(HttpStatusCode.OK, (await api.SendAsync(HttpMethod.Get, "/session", token)).StatusCode);
            Assert.True(await valkey.Database.KeyExistsAsync(EntryKey(token)));
        }
    }

    private static async Task AssertSessionInvalidAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(
            "store.buyer.session_invalid",
            (string?)JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["code"]);
    }

    private static RedisKey EntryKey(string token) => $"qep:sess:buyer:{BuyerTokenCodec.Hash(token)}";

    /// <summary>El Valkey de la clase y una conexión directa para mirar las claves.</summary>
    public sealed class SharedValkey : IAsyncLifetime
    {
        private IContainer _container = null!;
        private ConnectionMultiplexer _multiplexer = null!;

        public string ConnectionString { get; private set; } = string.Empty;

        public IDatabase Database => _multiplexer.GetDatabase();

        public async ValueTask InitializeAsync()
        {
            _container = await ValkeyTestServer.StartAsync(TestContext.Current.CancellationToken);
            ConnectionString = ValkeyTestServer.ConnectionString(_container);
            _multiplexer = await ConnectionMultiplexer.ConnectAsync(ConnectionString);
        }

        public async ValueTask DisposeAsync()
        {
            await _multiplexer.DisposeAsync();
            await _container.DisposeAsync();
        }
    }

    /// <summary>Cuenta los comandos de <c>BuyersDbContext</c> que tocan <c>buyers.sessions</c>.</summary>
    private sealed class SessionCommandCounter : DbCommandInterceptor
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Reset() => Interlocked.Exchange(ref _count, 0);

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Inspect(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Inspect(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Inspect(command);
            return ValueTask.FromResult(result);
        }

        private void Inspect(DbCommand command)
        {
            var text = command.CommandText;
            if (text.Contains("buyers.sessions", StringComparison.Ordinal)
                || text.Contains("\"buyers\".\"sessions\"", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _count);
            }
        }
    }
}
