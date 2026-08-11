using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Modules.Catalog.Application;
using Testcontainers.PostgreSql;

namespace Modules.Catalog.IntegrationTests;

public sealed class ProductApiTests
{
    private const string TenantId = "01900000-0000-7000-8000-000000000001";
    private const string SubjectId = "01900000-0000-7000-8000-000000000002";
    private const string OtherTenantId = "01900000-0000-7000-8000-0000000000ff";
    private const string OtherSubjectId = "01900000-0000-7000-8000-0000000000fe";

    // CA-CAT-02-01, first half: the route answers for the authenticated tenant. The list is
    // empty because nothing seeds catalog.products yet; the "only its own" half of the
    // criterion arrives with the create endpoint.
    [Fact]
    public async Task ListReturnsAnEmptyCatalogForANewTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(
            factory, SubjectId, TenantId, CatalogPermissions.ProductRead);

        var response = await client.GetAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ProductsResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Empty(body.Items);
    }

    // CA-CAT-02-02: the handler revalidates the caller's active tenant against the tenant in
    // the route before touching the repository, so this is 403 and not 404 — a 404 would leak
    // whether the other tenant's catalogue is empty.
    [Fact]
    public async Task ListForAnotherTenantIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        // Holds the permission: the 403 has to come from the tenant mismatch, not from a
        // missing permission, or the test would survive tenant isolation being removed.
        using var client = CreateClient(
            factory, OtherSubjectId, OtherTenantId, CatalogPermissions.ProductRead);

        var response = await client.GetAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // CA-CAT-02-03, read side: holding an unrelated permission is not holding this one.
    [Fact]
    public async Task ListWithoutTheReadPermissionIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(
            factory, SubjectId, TenantId, "tenancy.settings.read");

        var response = await client.GetAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
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

    // The development stub grants only the tenancy defaults when X-Permissions is absent
    // (DevelopmentAuthenticationHandler.ResolvePermissions), so a catalog permission has to
    // be asked for explicitly. Passing it per test keeps each 403 attributable: without this,
    // a cross-tenant test would pass simply because the caller held no catalog permission at
    // all, and would keep passing even if tenant isolation broke.
    private static HttpClient CreateClient(
        QepApiFactory factory,
        string subjectId,
        string tenantId,
        params string[] permissions)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", subjectId);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId);
        if (permissions.Length > 0)
        {
            client.DefaultRequestHeaders.Add("X-Permissions", string.Join(',', permissions));
        }

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
            // Pinned, never inherited from appsettings.json: with "infobip" and the Infobip
            // keys absent, NotificationsOptionsValidator fails at startup and every test in
            // this file dies before reaching its assertion. SDD-CT-17.
            builder.UseSetting("Notifications:EmailProvider", "log");
        }
    }
}
