using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Modules.Pricing.Application;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Modules.Pricing.IntegrationTests;

/// <summary>
/// El arranque compartido de las pruebas de integracion del modulo. Mismo patron que
/// CustomersApiHarness/CatalogApiHarness: una sola copia, para que ajustarla ajuste a todos los
/// archivos de prueba a la vez.
/// </summary>
internal static class PricingApiHarness
{
    // Deliberadamente distinto de TenancyDatabaseInitializer.DevelopmentTenantId
    // ("01900000-0000-7000-8000-000000000001", el mismo que usan CustomersApiHarness/
    // CatalogApiHarness): ese id se auto-provisiona en Development si tenancy.tenants está
    // vacía, así que un tenant de prueba con ese id ya tendría las cinco listas por defecto
    // sembradas antes de que el test cree nada — rompiendo cualquier aserción de "arranca
    // vacío". A ClientClassification/TaxRate no les importa (no siembran nada); a
    // DefaultPriceListsSeeder sí.
    public const string TenantId = "01900000-0000-7000-8000-000000000101";
    public const string SubjectId = "01900000-0000-7000-8000-000000000002";
    public const string OtherTenantId = "01900000-0000-7000-8000-0000000000ff";
    public const string OtherSubjectId = "01900000-0000-7000-8000-0000000000fe";

    public static string PriceListsUrl(string tenantId = TenantId) =>
        $"/api/v1/tenants/{tenantId}/pricing/price-lists";

    public static async Task<PostgreSqlContainer> StartDatabaseAsync()
    {
        var database = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            .Build();
        await database.StartAsync(TestContext.Current.CancellationToken);
        return database;
    }

    // El stub de desarrollo concede solo los defaults de tenancy cuando X-Permissions no esta, asi
    // que un permiso de pricing hay que pedirlo explicitamente.
    public static HttpClient CreateClient(
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

    public static HttpClient CreateManager(QepApiFactory factory, string tenantId = TenantId) =>
        CreateClient(
            factory,
            SubjectId,
            tenantId,
            PricingPermissions.PriceListRead,
            PricingPermissions.PriceListManage);

    public static async Task<PriceListResponse> CreatePriceListAsync(
        HttpClient client,
        string name = "Mayorista",
        string prefix = "MAY",
        string tenantId = TenantId)
    {
        var response = await client.PostAsJsonAsync(
            PriceListsUrl(tenantId),
            new { name, prefix },
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var priceList = await response.Content.ReadFromJsonAsync<PriceListResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(priceList);
        return priceList;
    }

    public static async Task<PriceListsResponse> ListAsync(
        HttpClient client, string tenantId = TenantId)
    {
        var response = await client.GetFromJsonAsync<PriceListsResponse>(
            PriceListsUrl(tenantId), TestContext.Current.CancellationToken);
        Assert.NotNull(response);
        return response;
    }

    public static async Task<string[]> ValidationFieldsAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("errors", out var errors)
            ? errors.EnumerateObject().Select(property => property.Name).ToArray()
            : [];
    }

    /// <summary>
    /// Inserta un tenant real en <c>tenancy.tenants</c>. Las pruebas de CRUD de listas no lo
    /// necesitan —el stub de auth no exige que el tenant del header exista—, pero el seed de las
    /// cinco listas por defecto sí: <c>DefaultPriceListsSeeder</c> siembra por cada fila de
    /// <c>ITenantRepository.ListAllIdsAsync</c>, así que sin un tenant real no hay a quién
    /// sembrarle nada.
    /// </summary>
    public static async Task CreateTenantAsync(QepApiFactory factory, string tenantId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        dbContext.Tenants.Add(Tenant.Create(
            Modules.Tenancy.Domain.TenantId.Parse(tenantId),
            $"tenant-{tenantId[..8]}",
            "Tenant de prueba",
            "es-CO",
            "America/Bogota",
            "yyyy-MM-dd",
            DateTimeOffset.UtcNow));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public sealed class QepApiFactory(string connectionString)
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
            builder.UseSetting("Notifications:EmailProvider", "log");
        }
    }
}
