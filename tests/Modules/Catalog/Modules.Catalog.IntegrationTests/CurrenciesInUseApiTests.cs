using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Modules.Catalog.Application;
using Testcontainers.PostgreSql;

namespace Modules.Catalog.IntegrationTests;

/// <summary>Spec D10: a currency is "in use" when at least one product of the tenant has a price
/// in it. Reports, Excel columns and the settings/bank-account selectors show only those.</summary>
public sealed class CurrenciesInUseApiTests
{
    private const string TenantId = "01900000-0000-7000-8000-0000000000e1";
    private const string OtherTenantId = "01900000-0000-7000-8000-0000000000e2";
    private const string SubjectId = "01900000-0000-7000-8000-0000000000e3";

    private static readonly string[] ManagePermissions =
    [
        CatalogPermissions.ProductRead, CatalogPermissions.ProductManage
    ];

    [Fact]
    public async Task ATenantWithoutProductsUsesNoCurrency()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var inUse = await client.GetFromJsonAsync<string[]>(Url(TenantId), TestContext.Current.CancellationToken);

        Assert.Empty(inUse!);
    }

    // Catalogue order (COP, USD, EUR) - not alphabetical, not first-seen - and another tenant's
    // EUR never leaks into this one.
    [Fact]
    public async Task CurrenciesComeInCatalogueOrderAndOnlyFromThisTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);
        using var other = CreateClient(factory, SubjectId, OtherTenantId, ManagePermissions);
        await CreateAsync(client, TenantId, "A-1", new() { ["USD"] = 10m });
        await CreateAsync(client, TenantId, "A-2", new() { ["COP"] = 40_000m, ["USD"] = 11m });
        await CreateAsync(other, OtherTenantId, "B-1", new() { ["EUR"] = 9m });

        var inUse = await client.GetFromJsonAsync<string[]>(Url(TenantId), TestContext.Current.CancellationToken);

        Assert.Equal(["COP", "USD"], inUse!);
    }

    private static string Url(string tenantId) => $"/api/v1/tenants/{tenantId}/catalog/currencies-in-use";

    private static async Task CreateAsync(
        HttpClient client, string tenantId, string code, Dictionary<string, decimal> prices) =>
        (await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/products",
            new { name = "Producto " + code, code, pricing = new { prices } },
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

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
            // Fijado, nunca heredado de appsettings.json. SDD-CT-17.
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
        }
    }
}
