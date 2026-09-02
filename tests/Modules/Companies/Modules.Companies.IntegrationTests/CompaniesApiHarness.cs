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

    /// <summary>
    /// El cuerpo de una cuenta bancaria. Sirve para armar la lista de un POST o un PUT sin repetir
    /// el objeto anonimo en cada prueba.
    /// </summary>
    public static object BankAccount(
        string accountNumber,
        string bankName = "Bancolombia",
        string currency = "COP") =>
        new { bankName, accountNumber, currency };

    private sealed record GeographyDepartmentDto(Guid Id, string DivipolaCode, string Name);

    private sealed record GeographyCityDto(Guid Id, string DivipolaCode, string Name, Guid DepartmentId);

    /// <summary>
    /// Una ciudad real (Geography no tiene tenant: los datos son los que siembra
    /// <c>GeographySeeder</c> en cada arranque), para satisfacer el <c>CityId</c> obligatorio de
    /// <c>CreateCompanyRequest</c>/<c>UpdateCompanyRequest</c> desde "feat(empresas): agregar
    /// departamento y ciudad". No se hardcodea ningun id: los dos endpoints de Geography son la
    /// unica fuente confiable de ids reales en esta base de prueba — mismo patron que
    /// <c>CustomersApiHarness.EnsureCityAsync</c>.
    /// </summary>
    public static async Task<Guid> EnsureCityIdAsync(HttpClient client)
    {
        var departments = await client.GetFromJsonAsync<List<GeographyDepartmentDto>>(
            "/api/v1/departments", TestContext.Current.CancellationToken);
        Assert.NotNull(departments);
        Assert.NotEmpty(departments);

        foreach (var department in departments)
        {
            var cities = await client.GetFromJsonAsync<List<GeographyCityDto>>(
                $"/api/v1/cities?departmentId={department.Id}",
                TestContext.Current.CancellationToken);
            if (cities is { Count: > 0 })
            {
                return cities[0].Id;
            }
        }

        throw new InvalidOperationException(
            "No seeded DIVIPOLA department has at least one city.");
    }

    /// <summary>
    /// Da de alta una empresa con una sola cuenta y devuelve la respuesta ya deserializada.
    ///
    /// Sigue tomando el numero suelto porque es lo que la mayoria de las pruebas necesita —una
    /// empresa cualquiera, distinguible por su numero—. Las que ejercen la coleccion arman el
    /// cuerpo a mano con <see cref="BankAccount"/>.
    ///
    /// <c>cityId</c> es opcional: sin uno explicito, resuelve el primero disponible con
    /// <see cref="EnsureCityIdAsync"/> — la mayoria de las pruebas no le importa cual ciudad,
    /// solo que la empresa se pueda crear.
    /// </summary>
    public static async Task<CompanyResponse> CreateCompanyAsync(
        HttpClient client,
        string name,
        string accountNumber,
        string taxId = "900.111.222-3",
        string? phone = null,
        string? email = null,
        string? address = null,
        string tenantId = TenantId,
        Guid? cityId = null)
    {
        var resolvedCityId = cityId ?? await EnsureCityIdAsync(client);
        var response = await client.PostAsJsonAsync(
            CompaniesUrl(tenantId),
            new
            {
                name,
                bankAccounts = new[] { BankAccount(accountNumber) },
                taxId,
                phone,
                email,
                address,
                cityId = resolvedCityId
            },
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
