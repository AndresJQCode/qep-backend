using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Modules.Identity.Infrastructure.Messaging;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Identity.IntegrationTests;

/// <summary>
/// Revocación de sesiones al suspender o quitar una membresía (<c>SessionRevocationWorker</c>).
/// El camino feliz de punta a punta, con cookie real, está en
/// <c>RealAuthenticationApiTests.SuspendingMembershipRevokesTheMembersActiveSession</c>; acá se
/// prueba lo que pasa cuando un mensaje falla: no puede trabar la cola, se reintenta con esperas
/// que crecen hasta un tope, y nunca se abandona.
///
/// Las sesiones se siembran por SQL y se leen de <c>identity.sessions</c>: con el stub de
/// desarrollo no hay cookie, y lo que importa es la columna <c>revoked_at</c>.
/// </summary>
public sealed class SessionRevocationTests
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(15);
    private static readonly string[] AdvisorRoles = ["advisor"];

    /// <summary>
    /// Un mensaje que no se puede procesar, más viejo que una suspensión sana, no puede dejarla sin
    /// revocar. Antes el lote compartía un solo scope y no tenía catch por mensaje: el primero que
    /// lanzaba cortaba el lote entero, y como el lote se ordena por llegada, quedaba primero en cada
    /// tick y ninguna revocación posterior se hacía nunca. El reloj está quieto para que el mensaje
    /// envenenado no venza su primera espera mientras la prueba corre.
    /// </summary>
    [Fact]
    public async Task APoisonMessageDoesNotBlockALaterRevocation()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        using var factory = new QepApiFactory(connectionString, services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(clock);
        });
        var (tenantId, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var member = await InviteAsync(ownerClient, tenantId, NewEmail());
        await ActivateMembershipAsync(connectionString, member.Id);
        var sessionId = await SeedSessionAsync(connectionString, member.UserId);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var poison = await InsertSuspendedEventAsync(
            connection, "{}", DateTimeOffset.UtcNow.AddHours(-1));

        Assert.Equal(HttpStatusCode.OK, (await SuspendAsync(ownerClient, tenantId, member.Id)).StatusCode);

        await WaitUntilAsync(async () => await IsRevokedAsync(connection, sessionId));
        Assert.Equal(0, await CountProcessedAsync(connection, poison));
        Assert.Equal(1, await AttemptsAsync(connection, poison));
    }

    /// <summary>
    /// Un mensaje que falla siempre queda reclamado hasta su próximo intento, con esperas que crecen
    /// (<see cref="ClaimedOutboxConsumer.LeaseFor"/>) hasta la última y se quedan ahí. Nunca se
    /// abandona: cuando la causa se arregla —acá, el payload—, el reintento siguiente revoca. El reloj
    /// es de la prueba, así que vencer una espera no cuesta minutos.
    /// </summary>
    [Fact]
    public async Task AFailingMessageKeepsBeingRetriedWithACappedBackoff()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        using var factory = new QepApiFactory(connectionString, services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(clock);
        });
        var (tenantId, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var member = await InviteAsync(ownerClient, tenantId, NewEmail());
        await ActivateMembershipAsync(connectionString, member.Id);
        var sessionId = await SeedSessionAsync(connectionString, member.UserId);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        // Un userId que no es un Guid: el worker falla al leer el payload, en cada intento.
        var poison = await InsertSuspendedEventAsync(
            connection, """{"userId": "not-a-guid"}""", DateTimeOffset.UtcNow.AddHours(-1));
        await WaitUntilAsync(async () => await AttemptsAsync(connection, poison) == 1);

        // La primera espera es corta: la falla más probable es un conflicto pasajero con
        // OrphanUserCleanupWorker sobre las mismas sesiones, y mientras tanto la sesión sigue viva.
        Assert.Equal(clock.UtcNow + TimeSpan.FromSeconds(5), await ClaimedUntilAsync(connection, poison));

        // Una suspensión sana procesada después del primer fallo prueba que corrió otro tick, y en
        // ese tick el mensaje fallido no se volvió a intentar: su espera no venció.
        await SuspendAMemberAndWaitForItsRevocationAsync(connectionString, connection, ownerClient, tenantId);
        Assert.Equal(1, await AttemptsAsync(connection, poison));

        var claims = Worker(factory).Claims;
        const int lastFailedAttempt = 5;
        for (var attempt = 2; attempt <= lastFailedAttempt; attempt++)
        {
            var expected = attempt;
            clock.Advance(claims.LeaseFor(attempt - 1) + TimeSpan.FromSeconds(1));
            await WaitUntilAsync(async () => await AttemptsAsync(connection, poison) == expected);
            Assert.Equal(0, await CountProcessedAsync(connection, poison));
        }

        // La espera se satura en la última de la curva, contada desde el instante congelado del reloj.
        var longestWait = claims.Leases[^1];
        Assert.Equal(clock.UtcNow + longestWait, await ClaimedUntilAsync(connection, poison));

        // Antes de que venza, otro tick no lo retoma.
        clock.Advance(longestWait - TimeSpan.FromMinutes(1));
        await SuspendAMemberAndWaitForItsRevocationAsync(connectionString, connection, ownerClient, tenantId);
        Assert.Equal(lastFailedAttempt, await AttemptsAsync(connection, poison));

        // Se arregla el payload: el reintento siguiente revoca la sesión y termina el mensaje.
        await ExecuteAsync(
            connection,
            "UPDATE platform.outbox_messages SET payload = jsonb_build_object('userId', @userId::text) WHERE id = @id",
            ("userId", member.UserId),
            ("id", poison));
        clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        await WaitUntilAsync(async () => await IsRevokedAsync(connection, sessionId));

        await WaitUntilAsync(async () => await CountProcessedAsync(connection, poison) == 1);
        Assert.Equal(lastFailedAttempt + 1, await AttemptsAsync(connection, poison));
    }

    /// <summary>
    /// Quitar a alguien también revoca sus sesiones, con su propio motivo. La persona sigue activa
    /// en otro tenant para que <c>OrphanUserCleanupWorker</c>, que consume el mismo evento, no borre
    /// al usuario —y con él las sesiones— antes de que se pueda mirar la fila.
    /// </summary>
    [Fact]
    public async Task RemovingAMembershipRevokesTheMembersSessions()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var email = NewEmail();
        var (tenantId, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var (otherTenantId, otherOwnerClient) = await RegisterTenantWithOwnerAsync(factory);
        var member = await InviteAsync(ownerClient, tenantId, email);
        var elsewhere = await InviteAsync(otherOwnerClient, otherTenantId, email);
        Assert.Equal(member.UserId, elsewhere.UserId);
        await ActivateMembershipAsync(connectionString, member.Id);
        await ActivateMembershipAsync(connectionString, elsewhere.Id);
        var sessionId = await SeedSessionAsync(connectionString, member.UserId);

        Assert.Equal(HttpStatusCode.OK, (await RemoveAsync(ownerClient, tenantId, member.Id)).StatusCode);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(async () => await IsRevokedAsync(connection, sessionId));

        Assert.Equal("membership_removed", await RevokedReasonAsync(connection, sessionId));
        await WaitUntilAsync(async () => await CountProcessedRemovalsAsync(connection, member.Id) == 1);
    }

    private static string NewEmail() => $"member-{Guid.NewGuid():N}@example.com";

    private static string NewSlug() => $"org-{Guid.NewGuid():N}"[..12];

    // Misma receta que OrphanUserCleanupTests: un owner Active y un cliente acotado a su tenant por
    // los headers del stub de desarrollo.
    private static async Task<(string TenantId, HttpClient Client)> RegisterTenantWithOwnerAsync(
        QepApiFactory factory)
    {
        using var bootstrapClient = CreateClient(
            factory, Guid.CreateVersion7().ToString(), Guid.CreateVersion7().ToString());
        bootstrapClient.DefaultRequestHeaders.Add("X-Email", NewEmail());
        bootstrapClient.DefaultRequestHeaders.Add("X-Email-Verified", "true");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/register-tenant")
        {
            Content = JsonContent.Create(new
            {
                displayName = "Acme Organization",
                slug = NewSlug(),
                defaultCulture = "es-CO",
                timeZone = "America/Bogota",
                dateFormat = "yyyy-MM-dd",
            }),
        };
        var response = await bootstrapClient.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var registered = await response.Content.ReadFromJsonAsync<RegisterPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(registered);

        var client = CreateClient(
            factory, Guid.CreateVersion7().ToString(), registered!.TenantId.ToString());
        return (registered.TenantId.ToString(), client);
    }

    private static async Task<MembershipPayload> InviteAsync(HttpClient client, string tenantId, string email)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/tenants/{tenantId}/memberships")
        {
            Content = JsonContent.Create(new { email, displayName = "Ana Pérez", roles = AdvisorRoles }),
        };
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var membership = await response.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(membership);
        return membership!;
    }

    private static async Task<HttpResponseMessage> SuspendAsync(
        HttpClient client,
        string tenantId,
        Guid membershipId)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/tenants/{tenantId}/memberships/{membershipId}/suspend");
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<HttpResponseMessage> RemoveAsync(
        HttpClient client,
        string tenantId,
        Guid membershipId)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/tenants/{tenantId}/memberships/{membershipId}/remove");
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    // La instancia que corre en la aplicación de la prueba: su curva es la que se ejerce.
    private static SessionRevocationWorker Worker(QepApiFactory factory) =>
        factory.Services.GetServices<IHostedService>().OfType<SessionRevocationWorker>().Single();

    // Una suspensión sana, de punta a punta, para saber que el worker corrió otro tick.
    private static async Task SuspendAMemberAndWaitForItsRevocationAsync(
        string connectionString,
        NpgsqlConnection connection,
        HttpClient ownerClient,
        string tenantId)
    {
        var member = await InviteAsync(ownerClient, tenantId, NewEmail());
        await ActivateMembershipAsync(connectionString, member.Id);
        var sessionId = await SeedSessionAsync(connectionString, member.UserId);
        Assert.Equal(HttpStatusCode.OK, (await SuspendAsync(ownerClient, tenantId, member.Id)).StatusCode);
        await WaitUntilAsync(async () => await IsRevokedAsync(connection, sessionId));
    }

    // Saltea la vuelta invitar-aceptar (que exige un login real de Google), igual que en Tenancy.
    private static Task ActivateMembershipAsync(string connectionString, Guid membershipId) =>
        ExecuteAsync(
            connectionString,
            "UPDATE tenancy.memberships SET state = 'Active', accepted_at = now() WHERE id = @id",
            ("id", membershipId));

    private static async Task<Guid> SeedSessionAsync(string connectionString, Guid userId)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(
            connectionString,
            """
            INSERT INTO identity.sessions
                (id, user_id, token_hash, created_at, last_seen_at, expires_at)
            VALUES (@id, @userId, @tokenHash, now(), now(), now() + interval '1 day')
            """,
            ("id", id),
            ("userId", userId),
            ("tokenHash", Guid.NewGuid().ToString("N")));
        return id;
    }

    // Un tenancy.membership-suspended.v1 escrito a mano. Va con processed_at para que el publicador
    // de Tenancy no lo despache: lo que se prueba es el consumidor de Identity, que lee el outbox
    // sin mirar esa columna.
    private static async Task<Guid> InsertSuspendedEventAsync(
        NpgsqlConnection connection,
        string payload,
        DateTimeOffset occurredAt)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(
            connection,
            """
            INSERT INTO platform.outbox_messages
                (id, event_name, payload, correlation_id, occurred_at, processed_at, attempts)
            VALUES (@id, @eventName, @payload::jsonb, 'session-revocation-tests', @occurredAt, now(), 0)
            """,
            ("id", id),
            ("eventName", SessionRevocationWorker.SuspendedEvent),
            ("payload", payload),
            ("occurredAt", occurredAt));
        return id;
    }

    private static async Task<bool> IsRevokedAsync(NpgsqlConnection connection, Guid sessionId) =>
        await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM identity.sessions WHERE id = @id AND revoked_at IS NOT NULL",
            ("id", sessionId)) == 1;

    private static Task<long> CountProcessedAsync(NpgsqlConnection connection, Guid messageId) =>
        ScalarAsync(
            connection,
            """
            SELECT COUNT(*) FROM identity.inbox_messages
            WHERE consumer = @consumer AND message_id = @id AND processed_at IS NOT NULL
            """,
            ("consumer", SessionRevocationWorker.Consumer),
            ("id", messageId));

    // El mensaje de quitar esa membresía, terminado por este consumidor. Su id es el EventId del
    // evento de dominio, que no se conoce desde afuera; se llega por el membershipId del payload.
    private static Task<long> CountProcessedRemovalsAsync(NpgsqlConnection connection, Guid membershipId) =>
        ScalarAsync(
            connection,
            """
            SELECT COUNT(*) FROM identity.inbox_messages inbox
            JOIN platform.outbox_messages outbox ON outbox.id = inbox.message_id
            WHERE inbox.consumer = @consumer
              AND inbox.processed_at IS NOT NULL
              AND outbox.event_name = @eventName
              AND (outbox.payload -> 'membershipId' ->> 'value') = @membershipId
            """,
            ("consumer", SessionRevocationWorker.Consumer),
            ("eventName", SessionRevocationWorker.RemovedEvent),
            ("membershipId", membershipId.ToString()));

    private static async Task<string> RevokedReasonAsync(NpgsqlConnection connection, Guid sessionId)
    {
        await using var command = CreateCommand(
            connection,
            "SELECT revoked_reason FROM identity.sessions WHERE id = @id",
            [("id", sessionId)]);
        return (string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    // Los intentos del mensaje, terminado o no; 0 si nunca se reclamó.
    private static Task<long> AttemptsAsync(NpgsqlConnection connection, Guid messageId) =>
        ScalarAsync(
            connection,
            """
            SELECT COALESCE(SUM(attempts), 0)::bigint FROM identity.inbox_messages
            WHERE consumer = @consumer AND message_id = @id
            """,
            ("consumer", SessionRevocationWorker.Consumer),
            ("id", messageId));

    private static async Task<DateTimeOffset> ClaimedUntilAsync(NpgsqlConnection connection, Guid messageId)
    {
        await using var command = CreateCommand(
            connection,
            """
            SELECT claimed_until FROM identity.inbox_messages
            WHERE consumer = @consumer AND message_id = @id
            """,
            [("consumer", SessionRevocationWorker.Consumer), ("id", messageId)]);
        var result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return new DateTimeOffset((DateTime)result!, TimeSpan.Zero);
    }

    private static async Task ExecuteAsync(
        string connectionString,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await ExecuteAsync(connection, sql, parameters);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = CreateCommand(connection, sql, parameters);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<long> ScalarAsync(
        NpgsqlConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = CreateCommand(connection, sql, parameters);
        var result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return (long)result!;
    }

    private static NpgsqlCommand CreateCommand(
        NpgsqlConnection connection,
        string sql,
        (string Name, object Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < PollTimeout)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);
        }

        Assert.Fail($"Condition was not met within {PollTimeout.TotalSeconds:0} seconds.");
    }

    private static async Task<PostgreSqlContainer> StartDatabaseAsync()
    {
        var database = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            .Build();
        await database.StartAsync(TestContext.Current.CancellationToken);
        return database;
    }

    private static HttpClient CreateClient(QepApiFactory factory, string subjectId, string tenantId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", subjectId);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId);
        return client;
    }

    private sealed record RegisterPayload(Guid TenantId, Guid OwnerUserId);

    private sealed record MembershipPayload(Guid Id, Guid UserId);

    // Reloj que sólo avanza cuando la prueba lo pide. Arranca truncado a microsegundos, igual que
    // SystemClock, para que lo que se guarda en timestamptz vuelva igual.
    private sealed class MutableClock(DateTimeOffset start) : IClock
    {
        private long _ticks = start.UtcTicks - (start.UtcTicks % TimeSpan.TicksPerMicrosecond);

        public DateTimeOffset UtcNow => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    private sealed class QepApiFactory(
        string connectionString,
        Action<IServiceCollection>? configureServices = null)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            if (configureServices is not null)
            {
                builder.ConfigureServices(configureServices);
            }

            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
            // Fijado y no heredado (SDD-CT-17): con el proveedor de correo de appsettings.json y
            // sus credenciales ausentes, el validador de opciones tira la aplicación al arrancar.
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
            builder.UseSetting("Registration:PublicTenantSignupEnabled", "true");
        }
    }
}
