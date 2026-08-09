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

    /// <summary>
    /// AUTH-05 / SDD-OD-04. Expiry is lazy: only an attempted sign-in transitions an
    /// invitation to Expired. Someone who never tries stays Invited with a past ExpiresAt,
    /// and re-inviting them returned that dead row unchanged — no new invitation, no
    /// failure, no warning, and no way for that person to ever join.
    ///
    /// The window is forced past in the database rather than by moving a clock: the API
    /// resolves time through the real IClock, and the point under test is what the handler
    /// does with a row whose window has already lapsed.
    /// </summary>
    [Fact]
    public async Task ReinvitingALapsedInvitationIssuesAFreshOne()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);
        var email = NewEmail();

        var first = await InviteAsync(client, TenantId, email);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var original = await first.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(original);

        await LapseInvitationAsync(database, original!.Id);

        var second = await InviteAsync(client, TenantId, email);

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var renewed = await second.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(renewed);
        Assert.Equal("Invited", renewed!.State);
        Assert.True(
            renewed.ExpiresAt > DateTimeOffset.UtcNow,
            "A renewed invitation must expire in the future.");
        Assert.True(renewed.Version > original.Version);

        // Renewed in place: (user_id, tenant_id) is UNIQUE, so a second row is impossible.
        // See SDD-CT-15.
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var rows = await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM tenancy.memberships WHERE user_id = @userId AND tenant_id = @tenantId",
            ("userId", original.UserId),
            ("tenantId", Guid.Parse(TenantId)));
        Assert.Equal(1, rows);
        Assert.Equal(original.Id, renewed.Id);
    }

    /// <summary>
    /// The renewal has to reach the person: InvitationDeliveryWorker sends the email off
    /// the outbox event, so a renewal that persists without re-emitting it is a silent
    /// no-op from the invitee's point of view.
    /// </summary>
    [Fact]
    public async Task ReinvitingALapsedInvitationEmitsTheInvitedEventAgain()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);
        var email = NewEmail();

        var first = await InviteAsync(client, TenantId, email);
        var original = await first.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(original);
        await LapseInvitationAsync(database, original!.Id);

        var second = await InviteAsync(client, TenantId, email);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var events = await ScalarAsync(
            connection,
            """
            SELECT COUNT(*) FROM platform.outbox_messages
            WHERE event_name = 'tenancy.membership-invited.v1'
              AND payload::text LIKE '%' || @membershipId || '%'
            """,
            ("membershipId", original.Id.ToString()));
        Assert.Equal(2, events);

        var audit = await ScalarAsync(
            connection,
            """
            SELECT COUNT(*) FROM audit.entries
            WHERE action = 'tenancy.membership.invited' AND resource_id = @membershipId
            """,
            ("membershipId", original.Id.ToString()));
        Assert.Equal(2, audit);
    }

    /// <summary>
    /// CA-AUTH-05-12: a live invitation stays untouched. Renewing it would invalidate the
    /// link already sitting in someone's inbox and silently move the deadline.
    /// </summary>
    [Fact]
    public async Task ReinvitingALiveInvitationRemainsAnIdempotentNoOp()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);
        var email = NewEmail();

        var first = await InviteAsync(client, TenantId, email);
        var original = await first.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(original);

        var second = await InviteAsync(client, TenantId, email);

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var repeated = await second.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(repeated);
        Assert.Equal(original!.Id, repeated!.Id);
        Assert.Equal(original.Version, repeated.Version);
        // Compared to the microsecond: the first response carries the in-memory value and
        // the second one comes back from Postgres, which stores microseconds. A stricter
        // comparison fails on sub-microsecond ticks and proves nothing about the no-op.
        Assert.True(
            (repeated.ExpiresAt - original.ExpiresAt).Duration() < TimeSpan.FromMilliseconds(1),
            "A no-op must not move the expiry window.");
    }

    /// <summary>
    /// CA-AUTH-05-12 through the real path. The unit test for an Active membership calls
    /// Reinvite() directly, which the handler never does for that state — it short-circuits
    /// first. Without this, the criterion looked covered and the production behaviour was
    /// untested. Found by the AUTH-05 review.
    /// </summary>
    [Fact]
    public async Task ReinvitingAnActiveMemberIsAnIdempotentNoOp()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);
        var email = NewEmail();

        var first = await InviteAsync(client, TenantId, email);
        var original = await first.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(original);

        // Activated in the database rather than by signing in: acceptance is a side effect
        // of a real login, and this test is about what a second invitation does to an
        // already-active member.
        await SetStateAsync(database, original!.Id, "Active");

        var second = await InviteAsync(client, TenantId, email);

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var repeated = await second.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(repeated);
        Assert.Equal(original.Id, repeated!.Id);
        Assert.Equal("Active", repeated.State);
        Assert.Equal(original.Version, repeated.Version);
    }

    /// <summary>
    /// A membership an administrator suspended is not reopened by inviting the person
    /// again. Before AUTH-05 this path inserted a second row and died on the
    /// (user_id, tenant_id) UNIQUE index, surfacing as a 500; now it is a stated 422.
    /// Whether re-inviting *should* restore them is SDD-OD-13, a product decision.
    /// </summary>
    [Fact]
    public async Task ReinvitingASuspendedMemberIsRefusedWithoutRestoringThem()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);
        var email = NewEmail();

        var first = await InviteAsync(client, TenantId, email);
        var original = await first.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(original);
        await SetStateAsync(database, original!.Id, "Suspended");

        var second = await InviteAsync(client, TenantId, email);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var state = await QueryRowAsync(
            connection,
            "SELECT state FROM tenancy.memberships WHERE id = @id",
            ("id", original.Id));
        Assert.NotNull(state);
        Assert.Equal("Suspended", state![0]);
    }

    private static async Task SetStateAsync(
        PostgreSqlContainer database,
        Guid membershipId,
        string state)
    {
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "UPDATE tenancy.memberships SET state = @state WHERE id = @id",
            connection);
        command.Parameters.AddWithValue("state", state);
        command.Parameters.AddWithValue("id", membershipId);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
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
