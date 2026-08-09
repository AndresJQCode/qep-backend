using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

namespace Modules.Tenancy.IntegrationTests;

/// <summary>
/// Covers the authorization surface the SPA reads to decide what to render.
///
/// The catalog endpoint already existed; <c>/authorization/me</c> is added by AUTH-04
/// because nothing exposed the caller's *effective* permissions: the catalog returns role
/// and permission definitions, and the session response carries only user, email and
/// tenants. Without it a client can only discover what it may do by attempting it and
/// reading the 403 — which cannot hide an action before it is attempted. See SDD-OD-10.
/// </summary>
public sealed class AuthorizationCatalogApiTests
{
    private const string TenantId = "01900000-0000-7000-8000-000000000001";
    private const string SubjectId = "01900000-0000-7000-8000-000000000002";
    private const string OtherTenantId = "01900000-0000-7000-8000-0000000000ff";
    private const string OtherSubjectId = "01900000-0000-7000-8000-0000000000fe";

    private static readonly string[] ReadOnlyPermissions =
        ["tenancy.membership.read", "tenancy.settings.read"];

    private static readonly string[] UnknownPermissionOnly = ["none.at.all"];

    private sealed record EffectivePermissionsResponse(
        Guid TenantId,
        Guid UserId,
        IReadOnlyCollection<string> Permissions);

    [Fact]
    public async Task EffectivePermissionsReturnsWhatTheCallerActuallyHas()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);
        client.DefaultRequestHeaders.Add(
            "X-Permissions",
            "tenancy.membership.read,tenancy.settings.read");

        var response = await client.GetAsync(
            $"/api/v1/tenants/{TenantId}/authorization/me",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<EffectivePermissionsResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Equal(Guid.Parse(TenantId), body!.TenantId);
        Assert.Equal(Guid.Parse(SubjectId), body.UserId);
        Assert.Equal(ReadOnlyPermissions, body.Permissions);
    }

    /// <summary>
    /// Asking "what may I do here" must not itself require a permission: requiring one
    /// makes the answer unreachable for exactly the subjects whose answer is "almost
    /// nothing", which is the case the UI most needs to render correctly.
    /// </summary>
    [Fact]
    public async Task EffectivePermissionsNeedsNoPermissionOfItsOwn()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);
        client.DefaultRequestHeaders.Add("X-Permissions", "none.at.all");

        var response = await client.GetAsync(
            $"/api/v1/tenants/{TenantId}/authorization/me",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<EffectivePermissionsResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Equal(UnknownPermissionOnly, body!.Permissions);
    }

    [Fact]
    public async Task EffectivePermissionsRejectsAnotherTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());

        // Authenticated for OtherTenant, asking about the seeded tenant.
        using var client = CreateClient(factory, OtherSubjectId, OtherTenantId);

        var response = await client.GetAsync(
            $"/api/v1/tenants/{TenantId}/authorization/me",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task EffectivePermissionsRejectsAnonymous()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            $"/api/v1/tenants/{TenantId}/authorization/me",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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
            // Pinned, not inherited: appsettings.json carries whatever provider the product
            // is deployed with, and an integration suite that depends on that ends up
            // depending on the credentials of whoever runs it. With "infobip" and the
            // Infobip keys absent — CI, a fresh clone — NotificationsOptionsValidator fails
            // at startup and every test in the file dies before reaching its assertion.
            // The log channel is the development default (SDD-CT-03). SDD-CT-17.
            builder.UseSetting("Notifications:EmailProvider", "log");
        }
    }
}
