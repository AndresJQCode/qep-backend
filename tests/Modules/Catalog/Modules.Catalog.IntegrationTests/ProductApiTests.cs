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

    // CA-CAT-02-01, primera mitad: la ruta responde para el tenant autenticado. La lista está
    // vacía porque todavía nada siembra catalog.products; la mitad de "sólo los suyos" del
    // criterio llega con el endpoint de creación.
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

    // CA-CAT-02-02: el handler revalida el tenant activo del llamador contra el tenant de la
    // ruta antes de tocar el repositorio, así que esto es 403 y no 404 — un 404 filtraría
    // si el catálogo del otro tenant está vacío.
    [Fact]
    public async Task ListForAnotherTenantIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        // Tiene el permiso: el 403 tiene que venir del tenant que no coincide, no de un permiso
        // faltante, o la prueba sobreviviría a que se quite el aislamiento de tenant.
        using var client = CreateClient(
            factory, OtherSubjectId, OtherTenantId, CatalogPermissions.ProductRead);

        var response = await client.GetAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // CA-CAT-02-03, lado de lectura: tener un permiso ajeno no es tener éste.
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

    // El stub de desarrollo concede sólo los defaults de tenancy cuando X-Permissions no está
    // (DevelopmentAuthenticationHandler.ResolvePermissions), así que un permiso de catalog hay
    // que pedirlo explícitamente. Pasarlo por prueba mantiene cada 403 atribuible: sin esto,
    // una prueba cross-tenant pasaría simplemente porque el llamador no tenía ningún permiso de
    // catalog, y seguiría pasando aunque se rompiera el aislamiento de tenant.
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
            // Fijado, nunca heredado de appsettings.json: con "infobip" y las claves de Infobip
            // ausentes, NotificationsOptionsValidator falla al arrancar y todas las pruebas de
            // este archivo mueren antes de llegar a su aserción. SDD-CT-17.
            builder.UseSetting("Notifications:EmailProvider", "log");
        }
    }
}
