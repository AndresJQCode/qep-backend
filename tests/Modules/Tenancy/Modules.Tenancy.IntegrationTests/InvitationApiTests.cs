using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Tenancy.IntegrationTests;

/// <summary>
/// El flujo de invitación por token: al invitar se genera un token cuyo hash queda en la
/// fila y cuyo valor plano viaja sólo por el outbox hacia el email; el link resultante se
/// consulta anónimo (GET /invitations/{token}) y se acepta con sesión
/// (POST /invitations/{token}/accept). El auto-accept del login sigue existiendo; esto se
/// suma, no lo reemplaza.
/// </summary>
public sealed class InvitationApiTests
{
    private const string TenantId = "01900000-0000-7000-8000-000000000001";
    private const string SubjectId = "01900000-0000-7000-8000-000000000002";
    private static readonly string[] DefaultRoles = ["advisor"];

    [Fact]
    public async Task InviteStoresOnlyTheTokenHashAndTheOutboxCarriesThePlainToken()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);

        var membership = await InviteAsync(client, NewEmail());

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var token = await GetTokenFromOutboxAsync(connection, membership.Id);
        Assert.False(string.IsNullOrWhiteSpace(token));

        var storedHash = await ScalarStringAsync(
            connection,
            "SELECT invitation_token_hash FROM tenancy.memberships WHERE id = @id",
            ("id", membership.Id));

        // En la base vive el hash, nunca el token: quien lea la tabla no puede armar links.
        var expectedHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(token!)));
        Assert.Equal(expectedHash, storedHash);
        Assert.NotEqual(token, storedHash);
    }

    [Fact]
    public async Task GetInvitationReturnsTenantNameEmailAndPendingStatus()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);
        var email = NewEmail();
        var membership = await InviteAsync(client, email);
        var token = await GetTokenAsync(database, membership.Id);

        // Anónimo a propósito: quien abre el link todavía no tiene sesión.
        using var anonymous = factory.CreateClient();
        var response = await anonymous.GetAsync(
            $"/api/v1/invitations/{token}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var invitation = await response.Content.ReadFromJsonAsync<InvitationPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(invitation);
        Assert.Equal(Guid.Parse(TenantId), invitation!.TenantId);
        Assert.Equal(email, invitation.Email);
        Assert.Equal("pending", invitation.Status);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var displayName = await ScalarStringAsync(
            connection,
            "SELECT display_name FROM tenancy.tenants WHERE id = @id",
            ("id", Guid.Parse(TenantId)));
        Assert.Equal(displayName, invitation.TenantName);
    }

    [Fact]
    public async Task GetInvitationWithAnUnknownTokenIs404()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());

        using var anonymous = factory.CreateClient();
        var response = await anonymous.GetAsync(
            "/api/v1/invitations/unknown-token",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetInvitationForALapsedInvitationReportsExpired()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);
        var membership = await InviteAsync(client, NewEmail());
        var token = await GetTokenAsync(database, membership.Id);
        await LapseInvitationAsync(database, membership.Id);

        using var anonymous = factory.CreateClient();
        var response = await anonymous.GetAsync(
            $"/api/v1/invitations/{token}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var invitation = await response.Content.ReadFromJsonAsync<InvitationPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal("expired", invitation!.Status);
    }

    [Fact]
    public async Task AcceptWithoutASessionIs401()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);
        var membership = await InviteAsync(client, NewEmail());
        var token = await GetTokenAsync(database, membership.Id);

        using var anonymous = factory.CreateClient();
        var response = await anonymous.PostAsync(
            $"/api/v1/invitations/{token}/accept",
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AcceptByTheInvitedUserActivatesWithAuditAndOutboxEventAndIsIdempotent()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);
        var membership = await InviteAsync(client, NewEmail());
        var token = await GetTokenAsync(database, membership.Id);

        // La sesión del propio invitado (stub por headers, igual que el resto de la suite).
        using var invited = CreateClient(factory, membership.UserId.ToString(), TenantId);
        var response = await invited.PostAsync(
            $"/api/v1/invitations/{token}/accept",
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var state = await ScalarStringAsync(
            connection,
            "SELECT state FROM tenancy.memberships WHERE id = @id",
            ("id", membership.Id));
        Assert.Equal("Active", state);

        // Auditada con el propio usuario como actor, y con el evento de outbox en la misma
        // unidad de trabajo — igual que la aceptación automática del login.
        var actor = await ScalarStringAsync(
            connection,
            """
            SELECT actor_id::text FROM audit.entries
            WHERE action = 'tenancy.membership.accepted' AND resource_id = @membershipId
            """,
            ("membershipId", membership.Id.ToString()));
        Assert.Equal(membership.UserId.ToString(), actor);

        var events = await ScalarAsync(
            connection,
            """
            SELECT COUNT(*) FROM platform.outbox_messages
            WHERE event_name = 'tenancy.membership-accepted.v1'
              AND payload::text LIKE '%' || @membershipId || '%'
            """,
            ("membershipId", membership.Id.ToString()));
        Assert.Equal(1L, events);

        // Repetir el accept con la misma sesión es un no-op declarado, no un error.
        var repeated = await invited.PostAsync(
            $"/api/v1/invitations/{token}/accept",
            content: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, repeated.StatusCode);

        // El GET del mismo link ahora dice "accepted": es el vocabulario documentado del
        // contrato de invitaciones (pending/accepted/expired, lo que el frontend renderiza),
        // no el del filtro del roster, donde el mismo estado se llama "active".
        using var anonymous = factory.CreateClient();
        var lookup = await anonymous.GetAsync(
            $"/api/v1/invitations/{token}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, lookup.StatusCode);
        var invitation = await lookup.Content.ReadFromJsonAsync<InvitationPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal("accepted", invitation!.Status);
    }

    [Fact]
    public async Task AcceptByAnotherUserIs403WithTheMismatchCode()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);
        var membership = await InviteAsync(client, NewEmail());
        var token = await GetTokenAsync(database, membership.Id);

        using var otherUser = CreateClient(
            factory,
            Guid.CreateVersion7().ToString(),
            TenantId);
        var response = await otherUser.PostAsync(
            $"/api/v1/invitations/{token}/accept",
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal("tenancy.invitation.user_mismatch", problem?.Code);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var state = await ScalarStringAsync(
            connection,
            "SELECT state FROM tenancy.memberships WHERE id = @id",
            ("id", membership.Id));
        Assert.Equal("Invited", state);
    }

    [Fact]
    public async Task AcceptALapsedInvitationIs422AndMarksItExpired()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);
        var membership = await InviteAsync(client, NewEmail());
        var token = await GetTokenAsync(database, membership.Id);
        await LapseInvitationAsync(database, membership.Id);

        using var invited = CreateClient(factory, membership.UserId.ToString(), TenantId);
        var response = await invited.PostAsync(
            $"/api/v1/invitations/{token}/accept",
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal("tenancy.membership.invitation_expired", problem?.Code);

        // El vencimiento perezoso quedó persistido, igual que en el camino del login.
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var state = await ScalarStringAsync(
            connection,
            "SELECT state FROM tenancy.memberships WHERE id = @id",
            ("id", membership.Id));
        Assert.Equal("Expired", state);
    }

    // ---- Helpers ----

    private static string NewEmail() => $"invitee-{Guid.NewGuid():N}@example.com";

    private static async Task<MembershipPayload> InviteAsync(HttpClient client, string email)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/tenants/{TenantId}/memberships")
        {
            Content = JsonContent.Create(new { email, roles = DefaultRoles })
        };
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var membership = await response.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(membership);
        return membership!;
    }

    private static async Task<string> GetTokenAsync(
        PostgreSqlContainer database,
        Guid membershipId)
    {
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var token = await GetTokenFromOutboxAsync(connection, membershipId);
        Assert.False(string.IsNullOrWhiteSpace(token));
        return token!;
    }

    // El token plano no está en ninguna tabla de Tenancy: se pesca del payload del evento
    // de outbox, el único lugar por donde viaja rumbo al email.
    private static async Task<string?> GetTokenFromOutboxAsync(
        NpgsqlConnection connection,
        Guid membershipId)
    {
        var payload = await ScalarStringAsync(
            connection,
            """
            SELECT payload::text FROM platform.outbox_messages
            WHERE event_name = 'tenancy.membership-invited.v1'
              AND payload::text LIKE '%' || @membershipId || '%'
            ORDER BY occurred_at DESC
            LIMIT 1
            """,
            ("membershipId", membershipId.ToString()));
        if (payload is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(payload);
        return document.RootElement.TryGetProperty("token", out var token)
            ? token.GetString()
            : null;
    }

    private static async Task LapseInvitationAsync(
        PostgreSqlContainer database,
        Guid membershipId)
    {
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "UPDATE tenancy.memberships SET expires_at = @expiresAt WHERE id = @id",
            connection);
        command.Parameters.AddWithValue("expiresAt", DateTimeOffset.UtcNow.AddHours(-1));
        command.Parameters.AddWithValue("id", membershipId);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<string?> ScalarStringAsync(
        NpgsqlConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return result is DBNull or null ? null : (string)result;
    }

    private static async Task<long> ScalarAsync(
        NpgsqlConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
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

    private sealed record ProblemPayload(string Code);

    private sealed record InvitationPayload(
        Guid TenantId,
        string TenantName,
        string Email,
        string Status);

    private sealed record MembershipPayload(
        Guid Id,
        Guid UserId,
        string Email,
        Guid TenantId,
        string State,
        IReadOnlyCollection<string> Roles,
        DateTimeOffset InvitedAt,
        DateTimeOffset? AcceptedAt,
        DateTimeOffset ExpiresAt,
        long Version);

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
            // Fijado, no heredado: appsettings.json lleva el proveedor con el que se despliega el
            // producto, y una suite de integración que depende de eso termina dependiendo de las
            // credenciales de quien la corra (SDD-CT-17).
            builder.UseSetting("Notifications:EmailProvider", "log");
        }
    }
}
