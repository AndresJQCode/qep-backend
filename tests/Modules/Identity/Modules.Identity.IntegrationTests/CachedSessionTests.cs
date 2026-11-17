using System.Data.Common;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using BuildingBlocks.Application;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Modules.Identity.Application;
using Modules.Identity.Infrastructure.Messaging;
using Modules.Identity.Infrastructure.Persistence;
using Npgsql;
using StackExchange.Redis;
using static Modules.Identity.IntegrationTests.PasswordAuthApiHarness;

namespace Modules.Identity.IntegrationTests;

/// <summary>
/// La caché de sesión en Valkey de punta a punta (spec 2026-11-13, D-4): con <c>Valkey:Areas:Sessions</c> prendido, un request
/// con una sesión ya validada no lee <c>identity.sessions</c>, y todo camino que revoca o borra una sesión saca su entrada. El
/// host es el de auth real (cookie <c>qep_session</c>); cada prueba levanta su base y su fábrica, como el resto de las pruebas de
/// contraseña (el limitador comparte la partición <c>unknown</c>). Las que paran Valkey usan un contenedor propio.
/// </summary>
public sealed class CachedSessionTests(CachedSessionTests.SharedValkey valkey) : IClassFixture<CachedSessionTests.SharedValkey>
{
    private const string Pw = "correct horse battery";

    [Fact]
    public async Task TheSecondRequestDoesNotReadTheDatabase()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        var counter = new SessionCommandCounter();
        await using var factory = NewFactory(cs, valkey.ConnectionString, counter: counter);
        var token = await RegisterAndLoginAsync(factory, cs);

        Assert.Equal(HttpStatusCode.OK, (await MeAsync(factory, token)).StatusCode);
        Assert.True(counter.Count > 0, "El primer request tiene que leer la sesión de Postgres.");
        counter.Reset();

        Assert.Equal(HttpStatusCode.OK, (await MeAsync(factory, token)).StatusCode);
        Assert.Equal(0, counter.Count);
    }

    [Fact]
    public async Task TouchStillHappensAfterFiveMinutes()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        await using var factory = NewFactory(cs, valkey.ConnectionString, clock);
        var token = await RegisterAndLoginAsync(factory, cs);
        Assert.Equal(HttpStatusCode.OK, (await MeAsync(factory, token)).StatusCode);
        var before = await LastSeenAtAsync(cs, token);

        clock.Advance(TimeSpan.FromMinutes(6));

        Assert.Equal(HttpStatusCode.OK, (await MeAsync(factory, token)).StatusCode);
        Assert.True(await LastSeenAtAsync(cs, token) > before);
    }

    [Fact]
    public async Task LogoutRemovesTheEntry()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        await using var factory = NewFactory(cs, valkey.ConnectionString);
        var token = await RegisterAndLoginAsync(factory, cs);
        Assert.Equal(HttpStatusCode.OK, (await MeAsync(factory, token)).StatusCode);
        Assert.True(await valkey.Database.KeyExistsAsync(EntryKey(token)));

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await SendWithCookieAsync(factory, HttpMethod.Post, "/api/v1/auth/logout", token)).StatusCode);

        Assert.False(await valkey.Database.KeyExistsAsync(EntryKey(token)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await MeAsync(factory, token)).StatusCode);
    }

    [Fact]
    public async Task PasswordResetRemovesEveryEntryOfTheUser()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        await using var factory = NewFactory(cs, valkey.ConnectionString);
        var email = NewEmail();
        var userId = (await RegisterAndVerifyAsync(CreateClient(factory), cs, email, Pw)).UserId;
        var first = await LoginTokenAsync(factory, email);
        var second = await LoginTokenAsync(factory, email);
        Assert.Equal(HttpStatusCode.OK, (await MeAsync(factory, first)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await MeAsync(factory, second)).StatusCode);
        Assert.True(await valkey.Database.KeyExistsAsync(EntryKey(first)));
        Assert.True(await valkey.Database.KeyExistsAsync(EntryKey(second)));

        using var anonymous = CreateClient(factory);
        await ForgotAsync(anonymous, email);
        var resetToken = await LatestOutboxTokenAsync(cs, IdentityIntegrationEvents.PasswordResetRequested, userId);
        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(anonymous, resetToken, "a brand new password")).StatusCode);

        Assert.False(await valkey.Database.KeyExistsAsync(EntryKey(first)));
        Assert.False(await valkey.Database.KeyExistsAsync(EntryKey(second)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await MeAsync(factory, first)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await MeAsync(factory, second)).StatusCode);
    }

    [Fact]
    public async Task SessionRevocationWorkerRemovesTheEntries()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        await using var factory = NewFactory(cs, valkey.ConnectionString);
        var email = NewEmail();
        var userId = (await RegisterAndVerifyAsync(CreateClient(factory), cs, email, Pw)).UserId;
        var token = await LoginTokenAsync(factory, email);
        Assert.Equal(HttpStatusCode.OK, (await MeAsync(factory, token)).StatusCode);
        Assert.True(await valkey.Database.KeyExistsAsync(EntryKey(token)));

        // La suspensión, escrita a mano como en SessionRevocationTests: lo que se prueba es el consumidor de Identity.
        await ExecuteAsync(
            cs,
            """
            INSERT INTO platform.outbox_messages
                (id, event_name, payload, correlation_id, occurred_at, processed_at, attempts)
            VALUES (@id, @eventName, jsonb_build_object('userId', @userId::text), 'cached-session-tests', now(), now(), 0)
            """,
            ("id", Guid.CreateVersion7()),
            ("eventName", SessionRevocationWorker.SuspendedEvent),
            ("userId", userId));
        var worker = factory.Services.GetServices<IHostedService>().OfType<SessionRevocationWorker>().Single();
        await WaitUntilAsync(async () =>
        {
            await worker.TickAsync(Ct);
            return await CountActiveSessionsAsync(cs, userId) == 0;
        });

        Assert.False(await valkey.Database.KeyExistsAsync(EntryKey(token)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await MeAsync(factory, token)).StatusCode);
    }

    [Fact]
    public async Task OrphanCleanupRemovesTheEntries()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        // Sin SessionRevocationWorker: también saca la entrada al ver la baja, y lo que se prueba es el borrado del huérfano.
        await using var factory = NewFactory(cs, valkey.ConnectionString, withoutSessionRevocationWorker: true);
        var ownerEmail = NewEmail();
        var owner = await RegisterAndVerifyAsync(CreateClient(factory), cs, ownerEmail, Pw);
        using var ownerClient = CreateClient(factory);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(ownerClient, ownerEmail, Pw)).StatusCode);
        var member = await InviteAsync(ownerClient, cs, owner.TenantId, NewEmail());
        await ExecuteAsync(
            cs,
            "UPDATE tenancy.memberships SET state = 'Active', accepted_at = now() WHERE id = @id",
            ("id", member.MembershipId));
        var token = await SeedSessionAsync(cs, member.UserId);
        Assert.Equal(HttpStatusCode.OK, (await MeAsync(factory, token)).StatusCode);
        Assert.True(await valkey.Database.KeyExistsAsync(EntryKey(token)));

        using var removal = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v1/tenants/{owner.TenantId}/memberships/{member.MembershipId}/remove");
        removal.Headers.Add("X-Tenant-Id", owner.TenantId.ToString());
        removal.Headers.Add("X-Qep-Client", "web");
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.SendAsync(removal, Ct)).StatusCode);
        var worker = factory.Services.GetServices<IHostedService>().OfType<OrphanUserCleanupWorker>().Single();
        await WaitUntilAsync(async () =>
        {
            await worker.TickAsync(Ct);
            return await ScalarAsync(cs, "SELECT COUNT(*) FROM identity.users WHERE id = @id", ("id", member.UserId)) == 0;
        });

        Assert.False(await valkey.Database.KeyExistsAsync(EntryKey(token)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await MeAsync(factory, token)).StatusCode);
    }

    [Fact]
    public async Task WithValkeyDownASessionIsValidatedFromPostgres()
    {
        await using var database = await StartDatabaseAsync();
        await using var ownValkey = await ValkeyTestServer.StartAsync(Ct);
        var cs = database.GetConnectionString();
        await using var factory = NewFactory(cs, ValkeyTestServer.ConnectionString(ownValkey));
        var token = await RegisterAndLoginAsync(factory, cs);

        await ownValkey.StopAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, (await MeAsync(factory, token)).StatusCode);
    }

    // Lo que mide el riesgo de P2: con Valkey caído, la entrada que quedó no se puede servir, y la revocación hecha sólo en
    // Postgres (el DEL no llegó) se respeta.
    [Fact]
    public async Task ARevokedSessionIsNotServedWhileValkeyIsDown()
    {
        await using var database = await StartDatabaseAsync();
        await using var ownValkey = await ValkeyTestServer.StartAsync(Ct);
        var cs = database.GetConnectionString();
        await using var factory = NewFactory(cs, ValkeyTestServer.ConnectionString(ownValkey));
        var token = await RegisterAndLoginAsync(factory, cs);
        Assert.Equal(HttpStatusCode.OK, (await MeAsync(factory, token)).StatusCode);
        using (var direct = await ConnectionMultiplexer.ConnectAsync(ValkeyTestServer.ConnectionString(ownValkey)))
        {
            Assert.True(await direct.GetDatabase().KeyExistsAsync(EntryKey(token)));
        }

        await ownValkey.StopAsync(Ct);
        await ExecuteAsync(
            cs,
            "UPDATE identity.sessions SET revoked_at = now(), revoked_reason = 'test' WHERE token_hash = @hash",
            ("hash", HashOf(token)));

        Assert.Equal(HttpStatusCode.Unauthorized, (await MeAsync(factory, token)).StatusCode);
    }

    [Fact]
    public async Task AnInvalidSessionIsNotCached()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        await using var factory = NewFactory(cs, valkey.ConnectionString);
        var unknown = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.Equal(HttpStatusCode.Unauthorized, (await MeAsync(factory, unknown)).StatusCode);

        Assert.False(await valkey.Database.KeyExistsAsync(EntryKey(unknown)));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PasswordAuthApiFactory NewFactory(
        string cs,
        string valkeyConnectionString,
        IClock? clock = null,
        SessionCommandCounter? counter = null,
        bool withoutSessionRevocationWorker = false) =>
        new(cs, configure: builder =>
        {
            // Con UseSetting y no con ConfigureAppConfiguration: AddQepValkey decide qué registra al armar el contenedor.
            builder.UseSetting("Valkey:Enabled", "true");
            builder.UseSetting("Valkey:Areas:Sessions", "true");
            builder.UseSetting("ConnectionStrings:Valkey", valkeyConnectionString);
            builder.ConfigureServices(services =>
            {
                if (clock is not null)
                {
                    services.RemoveAll<IClock>();
                    services.AddSingleton(clock);
                }

                if (counter is not null)
                {
                    services.ConfigureDbContext<IdentityDbContext>(options => options.AddInterceptors(counter));
                }

                if (withoutSessionRevocationWorker)
                {
                    var descriptor = services.Single(service =>
                        service.ServiceType == typeof(IHostedService)
                        && service.ImplementationType == typeof(SessionRevocationWorker));
                    services.Remove(descriptor);
                }
            });
        });

    private static async Task<string> RegisterAndLoginAsync(PasswordAuthApiFactory factory, string cs)
    {
        var email = NewEmail();
        await RegisterAndVerifyAsync(CreateClient(factory), cs, email, Pw);
        return await LoginTokenAsync(factory, email);
    }

    // El token crudo sale del Set-Cookie del login: así cada request lo manda a mano y la cookie "vieja" sigue a mano después
    // del logout, que en un navegador la borraría.
    private static async Task<string> LoginTokenAsync(PasswordAuthApiFactory factory, string email)
    {
        using var client = CreateClient(factory);
        var response = await LoginAsync(client, email, Pw);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith("qep_session=", StringComparison.Ordinal));
        return cookie["qep_session=".Length..].Split(';')[0];
    }

    private static Task<HttpResponseMessage> MeAsync(PasswordAuthApiFactory factory, string token) =>
        SendWithCookieAsync(factory, HttpMethod.Get, "/api/v1/auth/me", token);

    private static async Task<HttpResponseMessage> SendWithCookieAsync(
        PasswordAuthApiFactory factory, HttpMethod method, string path, string token)
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
        });
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", $"qep_session={token}");
        request.Headers.Add("X-Qep-Client", "web");
        return await client.SendAsync(request, Ct);
    }

    // El mismo hash que SessionService: SHA-256 del token crudo, en hex minúsculas.
    private static string HashOf(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static RedisKey EntryKey(string token) => $"qep:sess:{HashOf(token)}";

    private static async Task<string> SeedSessionAsync(string cs, Guid userId)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        await ExecuteAsync(
            cs,
            """
            INSERT INTO identity.sessions (id, user_id, token_hash, created_at, last_seen_at, expires_at)
            VALUES (@id, @userId, @hash, now(), now(), now() + interval '1 day')
            """,
            ("id", Guid.CreateVersion7()),
            ("userId", userId),
            ("hash", HashOf(token)));
        return token;
    }

    private static async Task<DateTimeOffset> LastSeenAtAsync(string cs, string token)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT last_seen_at FROM identity.sessions WHERE token_hash = @hash", connection);
        command.Parameters.AddWithValue("hash", HashOf(token));
        return new DateTimeOffset((DateTime)(await command.ExecuteScalarAsync(Ct))!, TimeSpan.Zero);
    }

    private static Task<long> CountActiveSessionsAsync(string cs, Guid userId) =>
        ScalarAsync(
            cs,
            "SELECT COUNT(*) FROM identity.sessions WHERE user_id = @id AND revoked_at IS NULL",
            ("id", userId));

    private static async Task ExecuteAsync(string cs, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<long> ScalarAsync(string cs, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

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

    /// <summary>Cuenta los comandos de <c>IdentityDbContext</c> que tocan <c>identity.sessions</c>.</summary>
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

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Inspect(command);
            return result;
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

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            Inspect(command);
            return result;
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Inspect(command);
            return ValueTask.FromResult(result);
        }

        private void Inspect(DbCommand command)
        {
            var text = command.CommandText;
            if (text.Contains("identity.sessions", StringComparison.Ordinal)
                || text.Contains("\"identity\".\"sessions\"", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _count);
            }
        }
    }

    // Reloj que sólo avanza cuando la prueba lo pide, truncado a microsegundos como SystemClock.
    private sealed class MutableClock(DateTimeOffset start) : IClock
    {
        private long _ticks = start.UtcTicks - (start.UtcTicks % TimeSpan.TicksPerMicrosecond);

        public DateTimeOffset UtcNow => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }
}
