using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Tenancy.Application;
using Npgsql;

namespace Modules.Tenancy.IntegrationTests;

/// <summary>
/// The optional notice phone of a membership (spec 2026-11-22 section 5.5): the member's own door (<c>/me</c>), the admin's door (the
/// profile PUT) and who gets to see the numbers in the roster.
/// </summary>
public sealed class MembershipPhoneApiTests(SharedTenancyHost host) : IClassFixture<SharedTenancyHost>
{
    private const string Phone = "+573001234567";
    private const string OtherPhone = "+573009998877";
    private static readonly string[] AdvisorRoles = ["advisor"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OwnPhoneRoundTrips()
    {
        var factory = Factory();
        var (tenantId, _, ownerUserId) = await RegisterTenantWithOwnerAsync(factory);
        using var me = ClientFor(factory, ownerUserId, tenantId);

        var put = await PutOwnPhoneAsync(me, tenantId, Phone);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var saved = await put.Content.ReadFromJsonAsync<MinePayload>(Ct);
        Assert.Equal(Phone, saved!.Phone);
        Assert.Equal("Member", saved.PhoneSource);

        var get = await me.GetAsync($"/api/v1/tenants/{tenantId}/memberships/me", Ct);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var read = await get.Content.ReadFromJsonAsync<MinePayload>(Ct);
        Assert.Equal(Phone, read!.Phone);
        Assert.Equal("Member", read.PhoneSource);
        Assert.Equal(saved.MembershipId, read.MembershipId);
    }

    [Fact]
    public async Task OwnPhoneRejectsWhatIsNotE164()
    {
        var factory = Factory();
        var (tenantId, _, ownerUserId) = await RegisterTenantWithOwnerAsync(factory);
        using var me = ClientFor(factory, ownerUserId, tenantId);

        var response = await PutOwnPhoneAsync(me, tenantId, "3001234567");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemPayload>(Ct);
        Assert.Contains(problem!.Errors.Keys, key => string.Equals(key, "Phone", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OwnPhoneNullClears()
    {
        var factory = Factory();
        var (tenantId, _, ownerUserId) = await RegisterTenantWithOwnerAsync(factory);
        using var me = ClientFor(factory, ownerUserId, tenantId);
        await PutOwnPhoneAsync(me, tenantId, Phone);

        var response = await PutOwnPhoneAsync(me, tenantId, null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cleared = await response.Content.ReadFromJsonAsync<MinePayload>(Ct);
        Assert.Null(cleared!.Phone);
        Assert.Null(cleared.PhoneSource);
    }

    [Fact]
    public async Task MeIsForbiddenForAnotherTenant()
    {
        var factory = Factory();
        var (tenantId, _, ownerUserId) = await RegisterTenantWithOwnerAsync(factory);
        var (otherTenantId, _, _) = await RegisterTenantWithOwnerAsync(factory);
        using var me = ClientFor(factory, ownerUserId, tenantId);

        var get = await me.GetAsync($"/api/v1/tenants/{otherTenantId}/memberships/me", Ct);
        var put = await PutOwnPhoneAsync(me, otherTenantId, Phone);

        Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        var problem = await get.Content.ReadFromJsonAsync<ProblemPayload>(Ct);
        Assert.Equal("authorization.denied", problem!.Code);
    }

    [Fact]
    public async Task MeOnlyTouchesTheCallersMembership()
    {
        var factory = Factory();
        var (tenantId, ownerMembershipId, _) = await RegisterTenantWithOwnerAsync(factory);
        var (memberId, memberUserId) = await InviteActiveAsync(factory, tenantId);
        using var member = ClientFor(factory, memberUserId, tenantId);

        var response = await PutOwnPhoneAsync(member, tenantId, Phone);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Phone, await StoredPhoneAsync(factory, memberId));
        Assert.Null(await StoredPhoneAsync(factory, ownerMembershipId));
    }

    [Fact]
    public async Task AdminSetsThePhoneThroughTheProfilePut()
    {
        var factory = Factory();
        var (tenantId, _, ownerUserId) = await RegisterTenantWithOwnerAsync(factory);
        using var admin = ClientFor(factory, ownerUserId, tenantId);
        var (memberId, _) = await InviteActiveAsync(factory, tenantId);

        var response = await PutProfileAsync(admin, tenantId, memberId, 1, new { displayName = "Ana Pérez", phone = Phone });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"2\"", response.Headers.ETag?.Tag);
        var row = await response.Content.ReadFromJsonAsync<RowPayload>(Ct);
        Assert.Equal(Phone, row!.Phone);
        Assert.Equal("Member", row.PhoneSource);
    }

    // An old frontend never sends the field: what is stored stays.
    [Fact]
    public async Task ProfilePutWithoutPhoneKeepsIt()
    {
        var factory = Factory();
        var (tenantId, _, ownerUserId) = await RegisterTenantWithOwnerAsync(factory);
        using var admin = ClientFor(factory, ownerUserId, tenantId);
        var (memberId, _) = await InviteActiveAsync(factory, tenantId);
        await PutProfileAsync(admin, tenantId, memberId, 1, new { displayName = "Ana Pérez", phone = Phone });

        var response = await PutProfileAsync(admin, tenantId, memberId, 2, new { displayName = "Ana María Pérez" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Phone, await StoredPhoneAsync(factory, memberId));
        var row = await response.Content.ReadFromJsonAsync<RowPayload>(Ct);
        Assert.Equal(Phone, row!.Phone);
    }

    [Fact]
    public async Task ProfilePutWithNullPhoneClearsIt()
    {
        var factory = Factory();
        var (tenantId, _, ownerUserId) = await RegisterTenantWithOwnerAsync(factory);
        using var admin = ClientFor(factory, ownerUserId, tenantId);
        var (memberId, _) = await InviteActiveAsync(factory, tenantId);
        await PutProfileAsync(admin, tenantId, memberId, 1, new { displayName = "Ana Pérez", phone = Phone });

        var response = await PutProfileAsync(admin, tenantId, memberId, 2, new { displayName = "Ana Pérez", phone = (string?)null });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(await StoredPhoneAsync(factory, memberId));
    }

    [Fact]
    public async Task ProfilePutWithAStalePhoneVersionIs412()
    {
        var factory = Factory();
        var (tenantId, _, ownerUserId) = await RegisterTenantWithOwnerAsync(factory);
        using var admin = ClientFor(factory, ownerUserId, tenantId);
        var (memberId, memberUserId) = await InviteActiveAsync(factory, tenantId);
        using var member = ClientFor(factory, memberUserId, tenantId);
        await PutOwnPhoneAsync(member, tenantId, Phone);

        var response = await PutProfileAsync(admin, tenantId, memberId, 1, new { displayName = "Ana Pérez" });

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
    }

    [Fact]
    public async Task ListShowsPhonesOnlyToWhoManagesMembers()
    {
        var factory = Factory();
        var (tenantId, _, ownerUserId) = await RegisterTenantWithOwnerAsync(factory);
        using var admin = ClientFor(factory, ownerUserId, tenantId);
        var (memberId, _) = await InviteActiveAsync(factory, tenantId);
        await PutProfileAsync(admin, tenantId, memberId, 1, new { displayName = "Ana Pérez", phone = Phone });
        using var reader = ClientFor(factory, Guid.CreateVersion7(), tenantId);
        reader.DefaultRequestHeaders.Add("X-Permissions", "advisorship.read");

        var managed = await admin.GetFromJsonAsync<ListPayload>($"/api/v1/tenants/{tenantId}/memberships", Ct);
        var readOnly = await reader.GetFromJsonAsync<ListPayload>($"/api/v1/tenants/{tenantId}/memberships", Ct);

        Assert.Equal(Phone, Assert.Single(managed!.Items, item => item.Id == memberId).Phone);
        var hidden = Assert.Single(readOnly!.Items, item => item.Id == memberId);
        Assert.Null(hidden.Phone);
        Assert.Null(hidden.PhoneSource);
    }

    [Fact]
    public async Task ListShowsTheHrPhoneWhenThereIsNoOwn()
    {
        var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddScoped<IMemberPhoneDirectory, EveryoneHasAnHrPhone>()));
        var (tenantId, _, ownerUserId) = await RegisterTenantWithOwnerAsync(factory);
        using var admin = ClientFor(factory, ownerUserId, tenantId);
        var (memberId, _) = await InviteActiveAsync(factory, tenantId);

        var list = await admin.GetFromJsonAsync<ListPayload>($"/api/v1/tenants/{tenantId}/memberships", Ct);

        var row = Assert.Single(list!.Items, item => item.Id == memberId);
        Assert.Equal(OtherPhone, row.Phone);
        Assert.Equal("Hr", row.PhoneSource);
    }

    [Fact]
    public async Task PhoneChangeIsAuditedWithoutTheNumber()
    {
        var factory = Factory();
        var (tenantId, _, ownerUserId) = await RegisterTenantWithOwnerAsync(factory);
        using var admin = ClientFor(factory, ownerUserId, tenantId);
        var (memberId, memberUserId) = await InviteActiveAsync(factory, tenantId);
        using var member = ClientFor(factory, memberUserId, tenantId);
        await PutOwnPhoneAsync(member, tenantId, Phone);
        await PutProfileAsync(admin, tenantId, memberId, 2, new { displayName = "Ana Pérez", phone = OtherPhone });

        var rows = await AuditRowsAsync(factory, memberId);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Contains("phone", row.ChangedFields);
            Assert.DoesNotContain(Phone, row.Json, StringComparison.Ordinal);
            Assert.DoesNotContain(OtherPhone, row.Json, StringComparison.Ordinal);
        });
    }

    private QepApiFactory Factory() =>
        host.Factory(connectionString => new QepApiFactory(connectionString));

    private static Task<HttpResponseMessage> PutOwnPhoneAsync(HttpClient client, string tenantId, string? phone) =>
        client.PutAsJsonAsync($"/api/v1/tenants/{tenantId}/memberships/me/phone", new { phone }, Ct);

    private static async Task<HttpResponseMessage> PutProfileAsync(
        HttpClient client, string tenantId, Guid membershipId, long version, object body)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put, $"/api/v1/tenants/{tenantId}/memberships/{membershipId}/profile")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        return await client.SendAsync(request, Ct);
    }

    private static HttpClient ClientFor(WebApplicationFactory<Program> factory, Guid subjectId, string tenantId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", subjectId.ToString());
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId);
        return client;
    }

    // Registers a tenant to get an owner membership already Active (the only way there without a real Google login).
    private static async Task<(string TenantId, Guid OwnerMembershipId, Guid OwnerUserId)> RegisterTenantWithOwnerAsync(
        WebApplicationFactory<Program> factory)
    {
        using var bootstrap = ClientFor(factory, Guid.CreateVersion7(), Guid.CreateVersion7().ToString());
        bootstrap.DefaultRequestHeaders.Add("X-Email", $"owner-{Guid.NewGuid():N}@example.com");
        bootstrap.DefaultRequestHeaders.Add("X-Email-Verified", "true");
        var response = await bootstrap.PostAsJsonAsync(
            "/api/v1/auth/register-tenant",
            new
            {
                displayName = "Acme Organization",
                slug = $"org-{Guid.NewGuid():N}"[..12],
                defaultCulture = "es-CO",
                timeZone = "America/Bogota",
                dateFormat = "yyyy-MM-dd",
            },
            Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var registered = await response.Content.ReadFromJsonAsync<RegisterPayload>(Ct);
        var membershipId = await ScalarGuidAsync(
            factory,
            "SELECT id FROM tenancy.memberships WHERE tenant_id = @a AND user_id = @b",
            registered!.TenantId,
            registered.OwnerUserId);
        return (registered.TenantId.ToString(), membershipId, registered.OwnerUserId);
    }

    // Invites a member with the advisor role and flips it to Active in SQL (skips the Google login round trip).
    private static async Task<(Guid MembershipId, Guid UserId)> InviteActiveAsync(
        WebApplicationFactory<Program> factory, string tenantId)
    {
        using var owner = ClientFor(factory, Guid.CreateVersion7(), tenantId);
        var response = await owner.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/memberships",
            new { email = $"member-{Guid.NewGuid():N}@example.com", displayName = "Ana Pérez", roles = AdvisorRoles },
            Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var row = await response.Content.ReadFromJsonAsync<RowPayload>(Ct);
        await ExecuteAsync(
            factory, "UPDATE tenancy.memberships SET state = 'Active', accepted_at = now() WHERE id = @a", row!.Id);
        return (row.Id, row.UserId);
    }

    private static async Task<string?> StoredPhoneAsync(WebApplicationFactory<Program> factory, Guid membershipId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString(factory));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT phone_e164 FROM tenancy.memberships WHERE id = @a", connection);
        command.Parameters.AddWithValue("a", membershipId);
        var value = await command.ExecuteScalarAsync(Ct);
        return value as string;
    }

    private static async Task<IReadOnlyList<(string[] ChangedFields, string Json)>> AuditRowsAsync(
        WebApplicationFactory<Program> factory, Guid membershipId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString(factory));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT changed_fields::text, row_to_json(e)::text
            FROM audit.entries e
            WHERE resource_id = @a AND action IN ('tenancy.membership.phone_updated', 'tenancy.membership.profile_updated')
            ORDER BY occurred_at
            """,
            connection);
        command.Parameters.AddWithValue("a", membershipId.ToString());
        var rows = new List<(string[], string)>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            // changed_fields es un arreglo jsonb, no text[]: Npgsql no lo lee como string[] sin JSON dinámico.
            rows.Add((System.Text.Json.JsonSerializer.Deserialize<string[]>(reader.GetString(0))!, reader.GetString(1)));
        }

        return rows;
    }

    private static async Task<Guid> ScalarGuidAsync(WebApplicationFactory<Program> factory, string sql, Guid a, Guid b)
    {
        await using var connection = new NpgsqlConnection(ConnectionString(factory));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("a", a);
        command.Parameters.AddWithValue("b", b);
        return (Guid)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static async Task ExecuteAsync(WebApplicationFactory<Program> factory, string sql, Guid a)
    {
        await using var connection = new NpgsqlConnection(ConnectionString(factory));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("a", a);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static string ConnectionString(WebApplicationFactory<Program> factory) =>
        factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("QepDatabase")!;

    private sealed record RegisterPayload(Guid TenantId, Guid OwnerUserId);

    private sealed record MinePayload(Guid MembershipId, string? Phone, string? PhoneSource);

    private sealed record RowPayload(Guid Id, Guid UserId, string? Phone, string? PhoneSource);

    private sealed record ListPayload(IReadOnlyList<RowPayload> Items);

    private sealed record ProblemPayload(string Code);

    private sealed record ValidationProblemPayload(IReadOnlyDictionary<string, string[]> Errors);

    // Stands in for the HR directory: every member the list asks about has a phone there.
    private sealed class EveryoneHasAnHrPhone : IMemberPhoneDirectory
    {
        public Task<IReadOnlyDictionary<Guid, string>> FindHrPhonesAsync(
            Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(memberIds.ToDictionary(id => id, _ => OtherPhone));
    }

    private sealed class QepApiFactory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            // Pinned, never inherited: the developer user-secrets may turn the seed on.
            builder.UseSetting("Seed:Enabled", "false");
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
            builder.UseSetting("Registration:PublicTenantSignupEnabled", "true");
        }
    }
}
