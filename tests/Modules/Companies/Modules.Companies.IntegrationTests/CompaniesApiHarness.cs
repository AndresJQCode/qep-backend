using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Modules.Companies.Application;
using Testcontainers.PostgreSql;

namespace Modules.Companies.IntegrationTests;

/// <summary>
/// El arranque compartido de las pruebas de integracion del modulo.
///
/// Catalog repite este mismo bloque —contenedor, factory y cabeceras del stub— textualmente en
/// sus ocho archivos de prueba. Aca vive una sola vez: es el mismo arranque en los tres archivos,
/// y con ocho copias basta con que alguien ajuste una para que las demas prueben contra otra
/// configuracion sin que nada avise.
/// </summary>
internal static class CompaniesApiHarness
{
    public const string TenantId = "01900000-0000-7000-8000-000000000001";
    public const string SubjectId = "01900000-0000-7000-8000-000000000002";
    public const string OtherTenantId = "01900000-0000-7000-8000-0000000000ff";
    public const string OtherSubjectId = "01900000-0000-7000-8000-0000000000fe";

    public static string CompaniesUrl(string tenantId = TenantId) =>
        $"/api/v1/tenants/{tenantId}/companies";

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

    // El stub de desarrollo concede solo los defaults de tenancy cuando X-Permissions no esta
    // (DevelopmentAuthenticationHandler.ResolvePermissions), asi que un permiso de companies hay
    // que pedirlo explicitamente. Pasarlo por prueba mantiene cada 403 atribuible: sin esto, una
    // prueba cross-tenant pasaria simplemente porque el llamador no tenia ningun permiso del
    // modulo, y seguiria pasando aunque se rompiera el aislamiento de tenant.
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

    public static HttpClient CreateManager(QepApiFactory factory) =>
        CreateClient(
            factory,
            SubjectId,
            TenantId,
            CompaniesPermissions.CompanyRead,
            CompaniesPermissions.CompanyManage);

    /// <summary>Da de alta una empresa y devuelve la respuesta ya deserializada.</summary>
    public static async Task<CompanyResponse> CreateCompanyAsync(
        HttpClient client,
        string name,
        string accountNumber,
        string taxId = "900.111.222-3",
        string? phone = null,
        string? email = null,
        string? address = null,
        string tenantId = TenantId)
    {
        var response = await client.PostAsJsonAsync(
            CompaniesUrl(tenantId),
            new { name, accountNumber, taxId, phone, email, address },
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var company = await response.Content.ReadFromJsonAsync<CompanyResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(company);
        return company;
    }

    /// <summary>
    /// Los nombres de campo del mapa <c>errors</c> de un 422 de validacion. Es el contrato que el
    /// formulario consume: <c>companyFieldErrors</c> descarta cualquier 422 sin este mapa, y
    /// mapea por nombre en PascalCase.
    /// </summary>
    public static async Task<string[]> ValidationFieldsAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("errors", out var errors)
            ? errors.EnumerateObject().Select(property => property.Name).ToArray()
            : [];
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
            // Fijado, nunca heredado de appsettings.json: con "infobip" y las claves de Infobip
            // ausentes, NotificationsOptionsValidator falla al arrancar y todas las pruebas de
            // este proyecto mueren antes de llegar a su asercion. SDD-CT-17.
            builder.UseSetting("Notifications:EmailProvider", "log");
        }
    }
}
