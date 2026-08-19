using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Modules.Customers.Application;
using Testcontainers.PostgreSql;

namespace Modules.Customers.IntegrationTests;

/// <summary>
/// El arranque compartido de las pruebas de integracion del modulo.
///
/// Vive una sola vez, como en companies: es el mismo arranque en todos los archivos de prueba, y
/// con una copia por archivo basta con que alguien ajuste una para que las demas prueben contra
/// otra configuracion sin que nada avise.
/// </summary>
internal static class CustomersApiHarness
{
    public const string TenantId = "01900000-0000-7000-8000-000000000001";
    public const string SubjectId = "01900000-0000-7000-8000-000000000002";
    public const string OtherTenantId = "01900000-0000-7000-8000-0000000000ff";
    public const string OtherSubjectId = "01900000-0000-7000-8000-0000000000fe";

    public static string CustomersUrl(string tenantId = TenantId) =>
        $"/api/v1/tenants/{tenantId}/customers";

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
    // (DevelopmentAuthenticationHandler.ResolvePermissions), asi que un permiso de customers hay
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
            CustomersPermissions.CustomerRead,
            CustomersPermissions.CustomerManage);

    public static HttpClient CreateImporter(QepApiFactory factory) =>
        CreateClient(
            factory,
            SubjectId,
            TenantId,
            CustomersPermissions.CustomerRead,
            CustomersPermissions.CustomerImport);

    /// <summary>El cuerpo minimo de un alta, con lo obligatorio y nada mas.</summary>
    public static object NewCustomerBody(
        string name = "Verde Esencial S.A.S.",
        string identificationType = "NIT",
        string identificationNumber = "900.123.456-1") =>
        new
        {
            name,
            identificationType,
            identificationNumber,
            withRetention = false
        };

    /// <summary>Da de alta un cliente y devuelve la respuesta ya deserializada.</summary>
    public static async Task<CustomerResponse> CreateCustomerAsync(
        HttpClient client,
        string name = "Verde Esencial S.A.S.",
        string identificationNumber = "900.123.456-1",
        string tenantId = TenantId)
    {
        var response = await client.PostAsJsonAsync(
            CustomersUrl(tenantId),
            NewCustomerBody(name: name, identificationNumber: identificationNumber),
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var customer = await response.Content.ReadFromJsonAsync<CustomerResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(customer);
        return customer;
    }

    public static async Task<CustomersResponse> ListAsync(HttpClient client, string query)
    {
        var response = await client.GetFromJsonAsync<CustomersResponse>(
            $"{CustomersUrl()}{query}",
            TestContext.Current.CancellationToken);
        Assert.NotNull(response);
        return response;
    }

    /// <summary>
    /// Los nombres de campo del mapa <c>errors</c> de un 422 de validacion. Es el contrato que el
    /// formulario consume: <c>customerFieldErrors</c> descarta cualquier 422 sin este mapa, y mapea
    /// por nombre en PascalCase.
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
