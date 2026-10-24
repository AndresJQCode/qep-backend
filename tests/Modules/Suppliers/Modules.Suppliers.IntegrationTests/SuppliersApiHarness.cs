using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Suppliers.Application;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Modules.Suppliers.IntegrationTests;

/// <summary>
/// El arranque compartido de las pruebas de integración del módulo. Copia el de Customers y no el
/// de Companies porque necesita dos cosas que éste no tiene: un tenant con fila (sin ella no hay
/// máscara de módulos y el reloj del tenant falla) y <c>configureServices</c> en la fábrica.
/// </summary>
internal static class SuppliersApiHarness
{
    public const string TenantId = "01900000-0000-7000-8000-000000000001";
    public const string SubjectId = "01900000-0000-7000-8000-000000000002";
    public const string OtherTenantId = "01900000-0000-7000-8000-0000000000ff";
    public const string OtherSubjectId = "01900000-0000-7000-8000-0000000000fe";

    public static string SuppliersUrl(string tenantId = TenantId) =>
        $"/api/v1/tenants/{tenantId}/suppliers";

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

    /// <summary>
    /// Siembra la fila del tenant y sus módulos. Con fila el stub de desarrollo enmascara los
    /// permisos por módulo, así que sólo así se puede probar «módulo apagado». Inserta únicamente
    /// las claves que faltan, por lo que se puede llamar dos veces sobre el mismo tenant.
    /// </summary>
    public static async Task SeedTenantAsync(
        QepApiFactory factory,
        string tenantId = TenantId,
        IEnumerable<TenantModuleKey>? modules = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var id = new Modules.Tenancy.Domain.TenantId(Guid.Parse(tenantId));
        var now = DateTimeOffset.UtcNow;
        var existing = await tenancy.Tenants.FindAsync(
            [id], TestContext.Current.CancellationToken);
        if (existing is null)
        {
            tenancy.Tenants.Add(Tenant.Create(
                id,
                "suppliers-api-tests",
                "Suppliers API Tests",
                "es-CO",
                "America/Bogota",
                "yyyy-MM-dd",
                MembershipId.New(),
                now));
            await tenancy.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        foreach (var key in modules ?? TenantModuleKeys.All)
        {
            await tenancy.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO tenancy.tenant_modules (tenant_id, module_key, enabled_at, source)
                VALUES ({id.Value}, {key.Value}, {now}, {TenantModuleSources.Seed})
                ON CONFLICT (tenant_id, module_key) DO NOTHING
                """,
                TestContext.Current.CancellationToken);
        }
    }

    // El stub concede sólo los defaults de tenancy cuando X-Permissions no está, así que el permiso
    // de suppliers se pide explícito: cada 403 queda atribuible al permiso o al tenant.
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
            SuppliersPermissions.Read,
            SuppliersPermissions.Manage);

    public static HttpClient CreateReader(QepApiFactory factory) =>
        CreateClient(factory, SubjectId, TenantId, SuppliersPermissions.Read);

    /// <summary>Abre base, fábrica y tenant sembrado: el arranque que repiten todas las pruebas.</summary>
    public static async Task<(PostgreSqlContainer Database, QepApiFactory Factory)> OpenAsync(
        IEnumerable<TenantModuleKey>? modules = null)
    {
        var database = await StartDatabaseAsync();
        var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory, modules: modules);
        return (database, factory);
    }

    private sealed record GeographyDepartmentDto(Guid Id, string DivipolaCode, string Name);

    private sealed record GeographyCityDto(Guid Id, string DivipolaCode, string Name, Guid DepartmentId);

    /// <summary>
    /// Una ciudad real: Geography la siembra en cada arranque y no se hardcodea ningún id. Mismo
    /// patrón que <c>CompaniesApiHarness.EnsureCityIdAsync</c>.
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
    /// Un cuerpo de escritura válido y colombiano. <paramref name="overrides"/> pisa campos por
    /// nombre (camelCase) para que cada prueba muestre sólo lo que la distingue.
    /// </summary>
    public static Dictionary<string, object?> ValidBody(
        Guid cityId, Dictionary<string, object?>? overrides = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["name"] = "Ferreteria El Tornillo S.A.S.",
            ["businessName"] = "El Tornillo",
            ["identificationType"] = "NIT",
            ["identificationNumber"] = "900.123.456-7",
            ["country"] = "CO",
            ["cityId"] = cityId,
            ["cityName"] = null,
            ["phone"] = "+57 300 123 4567",
            ["email"] = "ventas@tornillo.co",
            ["address"] = "Calle 10 # 5-20",
            ["paymentTermDays"] = 30,
            ["category"] = "Ferretería",
            ["notes"] = "Entrega los martes.",
            ["bankAccounts"] = new object[]
            {
                new { bankName = "Bancolombia", accountNumber = "123-456", currency = "COP" },
                new { bankName = "Davivienda", accountNumber = "789-012", currency = "USD" }
            },
            ["contacts"] = new object[]
            {
                new { name = "Marta Rojas", role = "Ventas", phone = "3001112233", email = "marta@tornillo.co" },
                new { name = "Luis Pardo", role = (string?)null, phone = (string?)null, email = (string?)null }
            }
        };

        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
            {
                body[key] = value;
            }
        }

        return body;
    }

    /// <summary>Da de alta un proveedor válido y devuelve el detalle ya deserializado.</summary>
    public static async Task<SupplierResponse> CreateSupplierAsync(
        HttpClient client,
        Dictionary<string, object?>? overrides = null,
        string tenantId = TenantId,
        Guid? cityId = null)
    {
        var resolvedCityId = cityId ?? await EnsureCityIdAsync(client);
        var response = await client.PostAsJsonAsync(
            SuppliersUrl(tenantId),
            ValidBody(resolvedCityId, overrides),
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var supplier = await response.Content.ReadFromJsonAsync<SupplierResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(supplier);
        return supplier;
    }

    /// <summary>
    /// Los nombres de campo del mapa <c>errors</c> de un 422 de validación: el contrato que el
    /// formulario consume, en PascalCase y con índice en las listas.
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

    public static async Task<string> BodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

    /// <param name="configureServices">
    /// Sustituciones de servicios para la prueba; corre después del registro de la aplicación.
    /// </param>
    public sealed class QepApiFactory(
        string connectionString, Action<IServiceCollection>? configureServices = null)
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
            // Fijado, nunca heredado de appsettings.json: con "infobip" y las claves ausentes el
            // validador de Notifications falla al arrancar. SDD-CT-17.
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");

            if (configureServices is not null)
            {
                builder.ConfigureServices(configureServices);
            }
        }
    }
}
