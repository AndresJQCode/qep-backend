using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.Persistence;
using Modules.Storage.Domain;
using Modules.Storage.Infrastructure.Persistence;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Identity.IntegrationTests;

/// <summary>
/// Borrado físico del usuario huérfano. Cuando una membresía se quita
/// (<c>POST /memberships/{id}/remove</c>) y la persona no deja huella en ningún otro módulo,
/// Identity elimina la fila de <c>identity.users</c> de forma asíncrona, consumiendo
/// <c>tenancy.membership-removed.v1</c> del Outbox de plataforma con su propio inbox.
///
/// El borrado nunca pasa en el handler de quitar: <c>RemoveMemberHandler</c> lee el correo
/// después del commit para armar la respuesta. Por eso todas las pruebas esperan al worker.
///
/// Las huellas que retienen al usuario las declara cada módulo por <c>IUserReferenceProbe</c>:
/// una membresía viva en otro tenant (Tenancy), una cotización/venta que referencia alguna de
/// sus membresías (Quotations) o un archivo del que es dueño (Storage). Auditoría y
/// notificaciones no retienen: son append-only y guardan snapshot.
/// </summary>
public sealed class OrphanUserCleanupTests
{
    private const string Consumer = "identity.orphan-user-cleanup";
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(15);
    private static readonly string[] AdvisorRoles = ["advisor"];

    [Fact]
    public async Task RemovingTheOnlyMembershipDeletesTheUserAndItsSessions()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var (tenantId, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var member = await InviteAsync(ownerClient, tenantId, NewEmail());
        await ActivateMembershipAsync(connectionString, member.Id);
        await SeedSessionAsync(connectionString, member.UserId);

        var removal = await RemoveAsync(ownerClient, tenantId, member.Id);
        Assert.Equal(HttpStatusCode.OK, removal.StatusCode);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(async () => await CountUsersAsync(connection, member.UserId) == 0);

        Assert.Equal(0, await CountSessionsAsync(connection, member.UserId));
        Assert.Equal(1, await CountInboxAsync(connection, member.Id));
        Assert.Equal(1, await CountAuditAsync(connection, member.UserId, "identity.user.deleted"));
    }

    [Fact]
    public async Task AnActiveMembershipInAnotherTenantKeepsTheUser()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var email = NewEmail();
        var (firstTenant, firstOwner) = await RegisterTenantWithOwnerAsync(factory);
        var (secondTenant, secondOwner) = await RegisterTenantWithOwnerAsync(factory);
        var first = await InviteAsync(firstOwner, firstTenant, email);
        var second = await InviteAsync(secondOwner, secondTenant, email);
        Assert.Equal(first.UserId, second.UserId);
        await ActivateMembershipAsync(connectionString, first.Id);
        await ActivateMembershipAsync(connectionString, second.Id);

        var removal = await RemoveAsync(firstOwner, firstTenant, first.Id);
        Assert.Equal(HttpStatusCode.OK, removal.StatusCode);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(async () => await CountInboxAsync(connection, first.Id) == 1);

        Assert.Equal(1, await CountUsersAsync(connection, first.UserId));
    }

    [Fact]
    public async Task ASuspendedMembershipElsewhereKeepsTheUser()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var email = NewEmail();
        var (firstTenant, firstOwner) = await RegisterTenantWithOwnerAsync(factory);
        var (secondTenant, secondOwner) = await RegisterTenantWithOwnerAsync(factory);
        var first = await InviteAsync(firstOwner, firstTenant, email);
        var second = await InviteAsync(secondOwner, secondTenant, email);
        await ActivateMembershipAsync(connectionString, first.Id);
        await SetMembershipStateAsync(connectionString, second.Id, "Suspended");

        var removal = await RemoveAsync(firstOwner, firstTenant, first.Id);
        Assert.Equal(HttpStatusCode.OK, removal.StatusCode);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(async () => await CountInboxAsync(connection, first.Id) == 1);

        Assert.Equal(1, await CountUsersAsync(connection, first.UserId));
    }

    [Fact]
    public async Task BeingTheAdvisorOfAQuotationKeepsTheUser()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var (tenantId, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var member = await InviteAsync(ownerClient, tenantId, NewEmail());
        await ActivateMembershipAsync(connectionString, member.Id);
        await SeedQuotationAsync(factory, Guid.Parse(tenantId), advisorMembershipId: member.Id);

        var removal = await RemoveAsync(ownerClient, tenantId, member.Id);
        Assert.Equal(HttpStatusCode.OK, removal.StatusCode);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(async () => await CountInboxAsync(connection, member.Id) == 1);

        Assert.Equal(1, await CountUsersAsync(connection, member.UserId));
    }

    [Fact]
    public async Task OwningAFileKeepsTheUser()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var (tenantId, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var member = await InviteAsync(ownerClient, tenantId, NewEmail());
        await ActivateMembershipAsync(connectionString, member.Id);
        await SeedFileAsync(factory, Guid.Parse(tenantId), ownerUserId: member.UserId);

        var removal = await RemoveAsync(ownerClient, tenantId, member.Id);
        Assert.Equal(HttpStatusCode.OK, removal.StatusCode);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(async () => await CountInboxAsync(connection, member.Id) == 1);

        Assert.Equal(1, await CountUsersAsync(connection, member.UserId));
    }

    /// <summary>
    /// Reentrega: se borra la fila del inbox para que el worker reclame el mensaje otra vez.
    /// El usuario ya no existe, así que la segunda pasada no tiene nada que borrar y sólo
    /// vuelve a marcar el inbox — sin excepción y sin frenar el loop.
    /// </summary>
    [Fact]
    public async Task ReprocessingTheSameMessageIsHarmless()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var (tenantId, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var member = await InviteAsync(ownerClient, tenantId, NewEmail());
        await ActivateMembershipAsync(connectionString, member.Id);

        var removal = await RemoveAsync(ownerClient, tenantId, member.Id);
        Assert.Equal(HttpStatusCode.OK, removal.StatusCode);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(async () => await CountUsersAsync(connection, member.UserId) == 0);
        await WaitUntilAsync(async () => await CountInboxAsync(connection, member.Id) == 1);

        await DeleteInboxAsync(connection, member.Id);
        await WaitUntilAsync(async () => await CountInboxAsync(connection, member.Id) == 1);

        Assert.Equal(0, await CountUsersAsync(connection, member.UserId));
        Assert.Equal(1, await CountAuditAsync(connection, member.UserId, "identity.user.deleted"));
    }

    /// <summary>
    /// Carrera con una invitación concurrente: sin serialización, Tenancy responde "sin huella",
    /// la invitación inserta su membresía y el DELETE la deja apuntando a un usuario borrado.
    /// Se reproduce sosteniendo desde afuera el advisory lock de <c>UserLifecycleLockKey</c>,
    /// como haría InviteMemberHandler: el worker tiene que esperar, y al entrar ya ve la
    /// membresía nueva.
    /// </summary>
    [Fact]
    public async Task AnInviteCommittedWhileTheLockIsHeldKeepsTheUser()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var email = NewEmail();
        var (tenantId, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var (secondTenant, _) = await RegisterTenantWithOwnerAsync(factory);
        var member = await InviteAsync(ownerClient, tenantId, email);
        await ActivateMembershipAsync(connectionString, member.Id);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            TestContext.Current.CancellationToken);
        await ExecuteAsync(
            connection,
            "SELECT pg_advisory_xact_lock(hashtext(@key))",
            ("key", UserLifecycleLockKey.For(email)));

        var removal = await RemoveAsync(ownerClient, tenantId, member.Id);
        Assert.Equal(HttpStatusCode.OK, removal.StatusCode);

        // El worker corre cada 3 s: a los 8 s ya reclamó el mensaje y tiene que estar bloqueado
        // en el lock, sin haber borrado ni marcado nada. Sin lock, acá el usuario ya no existe.
        await Task.Delay(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);
        Assert.Equal(1, await CountUsersAsync(connection, member.UserId));
        Assert.Equal(0, await CountInboxAsync(connection, member.Id));

        // La membresía nueva se commitea junto con el lock, como en el handler real.
        await ExecuteAsync(
            connection,
            """
            INSERT INTO tenancy.memberships
                (id, user_id, tenant_id, state, roles, origin, invited_at, accepted_at,
                 expires_at, version, created_at, updated_at)
            SELECT @id, user_id, @tenantId, 'Invited', roles, origin, now(), NULL,
                   now() + interval '7 days', version, now(), now()
            FROM tenancy.memberships WHERE id = @sourceId
            """,
            ("id", Guid.CreateVersion7()),
            ("tenantId", Guid.Parse(secondTenant)),
            ("sourceId", member.Id));
        await transaction.CommitAsync(TestContext.Current.CancellationToken);

        await WaitUntilAsync(async () => await CountInboxAsync(connection, member.Id) == 1);
        Assert.Equal(1, await CountUsersAsync(connection, member.UserId));
        Assert.Equal(0, await CountAuditAsync(connection, member.UserId, "identity.user.deleted"));
    }

    private static string NewEmail() => $"member-{Guid.NewGuid():N}@example.com";

    private static string NewSlug() => $"org-{Guid.NewGuid():N}"[..12];

    // Registra un tenant (signup público habilitado) para tener un owner ya Active y un cliente
    // acotado a ese tenant por los headers del stub de desarrollo — misma receta que
    // MembershipLifecycleApiTests en Tenancy.
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

    private static async Task<MembershipPayload> InviteAsync(
        HttpClient client,
        string tenantId,
        string email)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/tenants/{tenantId}/memberships")
        {
            Content = JsonContent.Create(new { email, roles = AdvisorRoles })
        };
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var membership = await response.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(membership);
        return membership!;
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

    // Saltea la vuelta invitar-aceptar (que exige un login real de Google), igual que en Tenancy.
    private static Task ActivateMembershipAsync(string connectionString, Guid membershipId) =>
        ExecuteAsync(
            connectionString,
            "UPDATE tenancy.memberships SET state = 'Active', accepted_at = now() WHERE id = @id",
            ("id", membershipId));

    private static Task SetMembershipStateAsync(
        string connectionString,
        Guid membershipId,
        string state) =>
        ExecuteAsync(
            connectionString,
            "UPDATE tenancy.memberships SET state = @state WHERE id = @id",
            ("state", state),
            ("id", membershipId));

    // identity.sessions no tiene FK a users (IdentityDbContext.ConfigureSession), así que el
    // worker tiene que borrarlas explícitamente: una fila viva acá es lo que lo prueba.
    private static Task SeedSessionAsync(string connectionString, Guid userId) =>
        ExecuteAsync(
            connectionString,
            """
            INSERT INTO identity.sessions
                (id, user_id, token_hash, created_at, last_seen_at, expires_at)
            VALUES (@id, @userId, @tokenHash, now(), now(), now() + interval '1 day')
            """,
            ("id", Guid.CreateVersion7()),
            ("userId", userId),
            ("tokenHash", Guid.NewGuid().ToString("N")));

    // Por el DbContext y no por la API: llegar a una cotización por HTTP exige cliente,
    // producto, escala y ciudad. Lo que importa acá es una sola columna: advisor_id.
    private static async Task SeedQuotationAsync(
        QepApiFactory factory,
        Guid tenantId,
        Guid advisorMembershipId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        var advisor = new MemberId(advisorMembershipId);
        dbContext.Quotations.Add(Quotation.Create(
            QuotationId.New(),
            tenantId,
            "COT-2026-0001",
            Guid.CreateVersion7(),
            advisor,
            validUntil: null,
            paymentMethod: null,
            notes: null,
            QuotationOverrides.Empty,
            advisor,
            DateTimeOffset.UtcNow));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task SeedFileAsync(QepApiFactory factory, Guid tenantId, Guid ownerUserId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<StorageDbContext>();
        dbContext.FileResources.Add(FileResource.CreatePendingUpload(
            FileResourceId.New(),
            tenantId,
            ownerUserId,
            FileOwnerType.User,
            "avatar.png",
            "image/png",
            1024,
            $"staging/{Guid.NewGuid():N}",
            DateTimeOffset.UtcNow));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static Task<long> CountUsersAsync(NpgsqlConnection connection, Guid userId) =>
        ScalarAsync(connection, "SELECT COUNT(*) FROM identity.users WHERE id = @id", ("id", userId));

    private static Task<long> CountSessionsAsync(NpgsqlConnection connection, Guid userId) =>
        ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM identity.sessions WHERE user_id = @id",
            ("id", userId));

    // El id del mensaje de outbox es el EventId del evento de dominio, que no se conoce desde
    // afuera; se llega por el payload, que lleva el membershipId.
    private static Task<long> CountInboxAsync(NpgsqlConnection connection, Guid membershipId) =>
        ScalarAsync(
            connection,
            """
            SELECT COUNT(*) FROM identity.inbox_messages inbox
            JOIN platform.outbox_messages outbox ON outbox.id = inbox.message_id
            WHERE inbox.consumer = @consumer
              AND outbox.event_name = 'tenancy.membership-removed.v1'
              AND (outbox.payload -> 'membershipId' ->> 'value') = @membershipId
            """,
            ("consumer", Consumer),
            ("membershipId", membershipId.ToString()));

    private static Task DeleteInboxAsync(NpgsqlConnection connection, Guid membershipId) =>
        ExecuteAsync(
            connection,
            """
            DELETE FROM identity.inbox_messages inbox
            USING platform.outbox_messages outbox
            WHERE outbox.id = inbox.message_id
              AND inbox.consumer = @consumer
              AND (outbox.payload -> 'membershipId' ->> 'value') = @membershipId
            """,
            ("consumer", Consumer),
            ("membershipId", membershipId.ToString()));

    private static Task<long> CountAuditAsync(NpgsqlConnection connection, Guid userId, string action) =>
        ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM audit.entries WHERE resource_id = @id AND action = @action",
            ("id", userId.ToString()),
            ("action", action));

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

    private static HttpClient CreateClient(
        QepApiFactory factory,
        string subjectId,
        string tenantId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", subjectId);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId);
        return client;
    }

    private sealed record RegisterPayload(Guid TenantId, Guid OwnerUserId);

    private sealed record MembershipPayload(Guid Id, Guid UserId, string State);

    private sealed class QepApiFactory(string connectionString)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
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
            builder.UseSetting("Registration:PublicTenantSignupEnabled", "true");
        }
    }
}
