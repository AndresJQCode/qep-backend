using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Tenancy.IntegrationTests;

public sealed class MembershipLifecycleApiTests
{
    private const string OtherTenantId = "01900000-0000-7000-8000-0000000000ff";
    private const string OtherSubjectId = "01900000-0000-7000-8000-0000000000fe";

    [Fact]
    public async Task SuspendActiveMembershipTransitionsToSuspended()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerMembershipId, _, ownerClient) =
            await RegisterTenantWithOwnerAsync(factory);
        var secondOwnerId = await InviteAsync(ownerClient, tenantId, NewEmail(), OwnerRoles);
        await ActivateMembershipAsync(factory.ConnectionString, secondOwnerId);

        var response = await SendActionAsync(ownerClient, tenantId, ownerMembershipId, "suspend");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var membership = await response.Content.ReadFromJsonAsync<MembershipListItemPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal("Suspended", membership!.State);
    }

    [Fact]
    public async Task SuspendNonActiveMembershipIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var invitedId = await InviteAsync(ownerClient, tenantId, NewEmail());

        var response = await SendActionAsync(ownerClient, tenantId, invitedId, "suspend");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task RemoveInvitedMembershipTransitionsToRemoved()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var invitedId = await InviteAsync(ownerClient, tenantId, NewEmail());

        var response = await SendActionAsync(ownerClient, tenantId, invitedId, "remove");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var membership = await response.Content.ReadFromJsonAsync<MembershipListItemPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal("Removed", membership!.State);
    }

    [Fact]
    public async Task RemoveAlreadyRemovedMembershipIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var invitedId = await InviteAsync(ownerClient, tenantId, NewEmail());
        await SendActionAsync(ownerClient, tenantId, invitedId, "remove");

        var response = await SendActionAsync(ownerClient, tenantId, invitedId, "remove");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task SuspendOwnMembershipIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerMembershipId, ownerUserId, _) =
            await RegisterTenantWithOwnerAsync(factory);
        using var selfClient = CreateClient(factory, ownerUserId.ToString(), tenantId);

        var response = await SendActionAsync(selfClient, tenantId, ownerMembershipId, "suspend");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task RemoveOwnMembershipIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerMembershipId, ownerUserId, _) =
            await RegisterTenantWithOwnerAsync(factory);
        using var selfClient = CreateClient(factory, ownerUserId.ToString(), tenantId);

        var response = await SendActionAsync(selfClient, tenantId, ownerMembershipId, "remove");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task SuspendLastActiveManagerIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerMembershipId, _, _) =
            await RegisterTenantWithOwnerAsync(factory);
        using var sameTenantOtherSubject = CreateClient(
            factory, Guid.CreateVersion7().ToString(), tenantId);

        var response = await SendActionAsync(
            sameTenantOtherSubject, tenantId, ownerMembershipId, "suspend");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task RemoveLastActiveManagerIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerMembershipId, _, _) =
            await RegisterTenantWithOwnerAsync(factory);
        using var sameTenantOtherSubject = CreateClient(
            factory, Guid.CreateVersion7().ToString(), tenantId);

        var response = await SendActionAsync(
            sameTenantOtherSubject, tenantId, ownerMembershipId, "remove");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task RemoveNonManagerMemberIsAllowedEvenAsOnlyOtherActiveMember()
    {
        // Proves the guard is manager-precise, not a blanket "last active membership"
        // check: the owner (manager) remains active, so removing the sole plain
        // member must succeed even though it empties the non-manager active pool.
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var memberId = await InviteAsync(ownerClient, tenantId, NewEmail(), MemberRoles);
        await ActivateMembershipAsync(factory.ConnectionString, memberId);

        var response = await SendActionAsync(ownerClient, tenantId, memberId, "remove");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UpdateRolesChangesMembershipRoles()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var memberId = await InviteAsync(ownerClient, tenantId, NewEmail(), MemberRoles);

        var response = await SendRolesAsync(ownerClient, tenantId, memberId, OwnerRoles);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var membership = await response.Content.ReadFromJsonAsync<MembershipListItemPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal(OwnerRoles, membership!.Roles);
        Assert.Equal(2, membership.Version);
    }

    [Fact]
    public async Task UpdateRolesForUnknownRoleIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var memberId = await InviteAsync(ownerClient, tenantId, NewEmail(), MemberRoles);

        var response = await SendRolesAsync(ownerClient, tenantId, memberId, ["tenancy.unknown"]);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task UpdateRolesCannotRemoveLastActiveManager()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerMembershipId, _, ownerClient) =
            await RegisterTenantWithOwnerAsync(factory);

        var response = await SendRolesAsync(
            ownerClient, tenantId, ownerMembershipId, MemberRoles);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task UpdateRolesRequiresIfMatch()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var memberId = await InviteAsync(ownerClient, tenantId, NewEmail(), MemberRoles);

        var response = await SendRolesAsync(
            ownerClient, tenantId, memberId, OwnerRoles, expectedVersion: null);

        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
    }

    [Fact]
    public async Task UpdateRolesWithStaleVersionIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var memberId = await InviteAsync(ownerClient, tenantId, NewEmail(), MemberRoles);

        var response = await SendRolesAsync(
            ownerClient, tenantId, memberId, OwnerRoles, expectedVersion: 99);

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
    }

    [Fact]
    public async Task ManageFromAnotherTenantIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerMembershipId, _, _) = await RegisterTenantWithOwnerAsync(factory);
        using var otherClient = CreateClient(factory, OtherSubjectId, OtherTenantId);

        var response = await SendActionAsync(otherClient, tenantId, ownerMembershipId, "suspend");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ManageOfUnknownMembershipIsNotFound()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);

        var response = await SendActionAsync(
            ownerClient, tenantId, Guid.CreateVersion7(), "suspend");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static readonly string[] MemberRoles = ["tenancy.member"];
    private static readonly string[] OwnerRoles = ["tenancy.owner"];

    private static string NewEmail() => $"member-{Guid.NewGuid():N}@example.com";

    private static string NewSlug() => $"org-{Guid.NewGuid():N}"[..12];

    // Registers a tenant (public signup enabled) to get an owner Membership already in
    // Active state (Membership.CreateActive, ADR 0016/0017) — the only way to reach
    // Active without a full Google-login round trip. Returns a client scoped to the
    // new tenant via dev-stub headers (default permission set = full owner grant).
    private static async Task<(string TenantId, Guid OwnerMembershipId, Guid OwnerUserId, HttpClient Client)>
        RegisterTenantWithOwnerAsync(QepApiFactory factory)
    {
        var ownerEmail = NewEmail();
        using (var bootstrapClient = CreateClient(
            factory, Guid.CreateVersion7().ToString(), Guid.CreateVersion7().ToString()))
        {
            bootstrapClient.DefaultRequestHeaders.Add("X-Email", ownerEmail);
            bootstrapClient.DefaultRequestHeaders.Add("X-Email-Verified", "true");

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                "/api/v1/auth/register-tenant")
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
            var response = await bootstrapClient.SendAsync(
                request, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var registered = await response.Content.ReadFromJsonAsync<RegisterPayload>(
                TestContext.Current.CancellationToken);
            Assert.NotNull(registered);

            var client = CreateClient(
                factory,
                Guid.CreateVersion7().ToString(),
                registered!.TenantId.ToString());

            var ownerMembershipId = await FindMembershipIdAsync(
                factory.ConnectionString, registered.TenantId, registered.OwnerUserId);

            return (
                registered.TenantId.ToString(),
                ownerMembershipId,
                registered.OwnerUserId,
                client);
        }
    }

    private static async Task<Guid> InviteAsync(
        HttpClient client,
        string tenantId,
        string email,
        IReadOnlyCollection<string>? roles = null)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/tenants/{tenantId}/memberships")
        {
            Content = JsonContent.Create(new { email, roles = roles ?? MemberRoles })
        };
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var membership = await response.Content.ReadFromJsonAsync<MembershipListItemPayload>(
            TestContext.Current.CancellationToken);
        return membership!.Id;
    }

    private static async Task<HttpResponseMessage> SendActionAsync(
        HttpClient client,
        string tenantId,
        Guid membershipId,
        string action)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/tenants/{tenantId}/memberships/{membershipId}/{action}");
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<HttpResponseMessage> SendRolesAsync(
        HttpClient client,
        string tenantId,
        Guid membershipId,
        IReadOnlyCollection<string> roles,
        long? expectedVersion = 1)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/v1/tenants/{tenantId}/memberships/{membershipId}/roles")
        {
            Content = JsonContent.Create(new { roles })
        };
        if (expectedVersion is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{expectedVersion}\"");
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<Guid> FindMembershipIdAsync(
        string connectionString,
        Guid tenantId,
        Guid userId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT id FROM tenancy.memberships WHERE tenant_id = @tenantId AND user_id = @userId",
            connection);
        command.Parameters.AddWithValue("tenantId", tenantId);
        command.Parameters.AddWithValue("userId", userId);
        var result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return (Guid)result!;
    }

    // Bypasses the invite-accept round trip (which requires a real Google login) so
    // tests can put a second membership into Active state directly.
    private static async Task ActivateMembershipAsync(string connectionString, Guid membershipId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "UPDATE tenancy.memberships SET state = 'Active', accepted_at = now() WHERE id = @id",
            connection);
        command.Parameters.AddWithValue("id", membershipId);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
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

    private sealed class QepApiFactory(string connectionString) : WebApplicationFactory<Program>
    {
        public string ConnectionString => connectionString;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
            // Pinned, not inherited: appsettings.json carries whatever provider the product
            // is deployed with, and an integration suite that depends on that ends up
            // depending on the credentials of whoever runs it. With "infobip" and the
            // Infobip keys absent — CI, a fresh clone — NotificationsOptionsValidator fails
            // at startup and every test in the file dies before reaching its assertion.
            // The log channel is the development default (SDD-CT-03). SDD-CT-17.
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Registration:PublicTenantSignupEnabled", "true");
        }
    }
}
