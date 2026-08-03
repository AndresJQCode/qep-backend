using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Tenancy.IntegrationTests;

public sealed class MembershipApiTests
{
    private const string TenantId = "01900000-0000-7000-8000-000000000001";
    private const string SubjectId = "01900000-0000-7000-8000-000000000002";
    private const string OtherTenantId = "01900000-0000-7000-8000-0000000000ff";
    private const string OtherSubjectId = "01900000-0000-7000-8000-0000000000fe";
    private static readonly string[] DefaultRoles = ["tenancy.member"];
    private static readonly string[] UnknownRoles = ["tenancy.unknown"];

    [Fact]
    public async Task InviteProvisionsUserMembershipAuditAndOutboxEvent()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);
        var email = NewEmail();

        var response = await InviteAsync(client, TenantId, email);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var membership = await response.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(membership);
        Assert.Equal("Invited", membership!.State);
        Assert.Equal(email, membership.Email);
        Assert.NotEqual(Guid.Empty, membership.UserId);
        // 72-hour invitation window, per ADR 0016.
        Assert.True(membership.ExpiresAt > membership.InvitedAt.AddHours(71));
        Assert.True(membership.ExpiresAt <= membership.InvitedAt.AddHours(72));

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        // Identity provisioned an invited user for the email.
        var user = await QueryRowAsync(
            connection,
            "SELECT status FROM identity.users WHERE email = @email",
            ("email", email));
        Assert.NotNull(user);
        Assert.Equal("Invited", user![0]);

        // Tenancy created the membership in Invited state.
        var membershipRow = await QueryRowAsync(
            connection,
            "SELECT state FROM tenancy.memberships WHERE id = @id",
            ("id", membership.Id));
        Assert.NotNull(membershipRow);
        Assert.Equal("Invited", membershipRow![0]);

        // Audit entry records the invitation.
        var audit = await QueryRowAsync(
            connection,
            """
            SELECT actor_id::text, outcome
            FROM audit.entries
            WHERE resource_id = @resourceId AND action = 'tenancy.membership.invited'
            """,
            ("resourceId", membership.Id.ToString()));
        Assert.NotNull(audit);
        Assert.Equal(SubjectId, audit![0]);
        Assert.Equal("success", audit[1]);

        // Outbox carries the correlated integration event, same unit of work.
        var outbox = await QueryRowAsync(
            connection,
            """
            SELECT correlation_id, payload::text
            FROM platform.outbox_messages
            WHERE event_name = 'tenancy.membership-invited.v1'
              AND payload::text LIKE @idPattern
            """,
            ("idPattern", $"%{membership.Id}%"));
        Assert.NotNull(outbox);
        Assert.False(string.IsNullOrWhiteSpace(outbox![0]));
    }

    [Fact]
    public async Task InviteToAnotherTenantIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        // Authenticated as OtherTenant, attempting to invite into the seeded tenant.
        using var client = CreateClient(factory, OtherSubjectId, OtherTenantId);

        var response = await InviteAsync(client, TenantId, NewEmail());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task InvitingSameEmailTwiceIsIdempotent()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);
        var email = NewEmail();

        var first = await InviteAsync(client, TenantId, email);
        var second = await InviteAsync(client, TenantId, email);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var firstMembership = await first.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        var secondMembership = await second.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(firstMembership);
        Assert.NotNull(secondMembership);
        Assert.Equal(firstMembership!.Id, secondMembership!.Id);
        Assert.Equal(firstMembership.UserId, secondMembership.UserId);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var count = await ScalarAsync(
            connection,
            "SELECT count(*) FROM tenancy.memberships WHERE user_id = @userId",
            ("userId", firstMembership.UserId));
        Assert.Equal(1L, count);
    }

    [Fact]
    public async Task ListReturnsInvitedMembersForTheirTenantOnly()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);
        var email = NewEmail();
        var invited = await InviteAsync(client, TenantId, email);
        var invitedMembership = await invited.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);

        var response = await client.GetAsync(
            $"/api/v1/tenants/{TenantId}/memberships",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var memberships = await response.Content.ReadFromJsonAsync<MembershipListItemPayload[]>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(memberships);
        var listed = Assert.Single(
            memberships!,
            membership => membership.Id == invitedMembership!.Id);
        Assert.Equal(email, listed.Email);
        Assert.All(memberships!, membership => Assert.Equal(TenantId, membership.TenantId.ToString()));
    }

    [Fact]
    public async Task ListForAnotherTenantIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, OtherSubjectId, OtherTenantId);

        var response = await client.GetAsync(
            $"/api/v1/tenants/{TenantId}/memberships",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AuthorizationCatalogReturnsRolesAndPermissions()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);

        var response = await client.GetAsync(
            $"/api/v1/tenants/{TenantId}/authorization/catalog",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var catalog = await response.Content.ReadFromJsonAsync<AuthorizationCatalogPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(catalog);
        Assert.False(string.IsNullOrWhiteSpace(catalog!.CatalogVersion));
        Assert.Contains(catalog!.Roles, role => role.Role == "tenancy.owner");
        Assert.Contains(catalog.Roles, role =>
            role.Role == "tenancy.owner" && role.RiskLevel == "high");
        Assert.Contains(catalog.Roles, role => role.Role == "tenancy.member");
        Assert.Contains(
            catalog.Permissions,
            permission =>
                permission.Permission == "tenancy.membership.manage" &&
                permission.DisplayName == "Gestionar miembros y roles");
    }

    [Fact]
    public async Task InviteWithUnknownRoleIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/tenants/{TenantId}/memberships")
        {
            Content = JsonContent.Create(new
            {
                email = NewEmail(),
                roles = UnknownRoles
            })
        };
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    private static string NewEmail() => $"invitee-{Guid.NewGuid():N}@example.com";

    private static async Task<HttpResponseMessage> InviteAsync(
        HttpClient client,
        string tenantId,
        string email)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/tenants/{tenantId}/memberships")
        {
            Content = JsonContent.Create(new { email, roles = DefaultRoles })
        };
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<string[]?> QueryRowAsync(
        NpgsqlConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await using var reader = await command.ExecuteReaderAsync(
            TestContext.Current.CancellationToken);
        if (!await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            return null;
        }

        var values = new string[reader.FieldCount];
        for (var index = 0; index < reader.FieldCount; index++)
        {
            values[index] = reader.GetValue(index).ToString() ?? string.Empty;
        }

        return values;
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

    private sealed record MembershipListItemPayload(
        Guid Id,
        Guid UserId,
        string? Email,
        Guid TenantId,
        string State,
        IReadOnlyCollection<string> Roles,
        DateTimeOffset InvitedAt,
        DateTimeOffset? AcceptedAt,
        DateTimeOffset ExpiresAt,
        long Version);

    private sealed record AuthorizationCatalogPayload(
        string CatalogVersion,
        IReadOnlyCollection<RoleCatalogItemPayload> Roles,
        IReadOnlyCollection<PermissionCatalogItemPayload> Permissions);

    private sealed record RoleCatalogItemPayload(
        string Role,
        string DisplayName,
        string Description,
        string Category,
        string RiskLevel,
        IReadOnlyCollection<string> Permissions);

    private sealed record PermissionCatalogItemPayload(
        string Permission,
        string DisplayName,
        string Description,
        string Category,
        string RiskLevel);

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
        }
    }
}
