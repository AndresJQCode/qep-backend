using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Modules.Ai.IntegrationTests;

/// <summary>
/// El arranque compartido de las pruebas de integración del módulo, copia de <c>SuppliersApiHarness</c>: un tenant con fila (sin ella no
/// hay máscara de módulos) y <c>configureServices</c> en la fábrica, para los dobles de los proveedores que llegan con las tareas
/// siguientes.
/// </summary>
internal static class AiApiHarness
{
    public const string TenantId = "01900000-0000-7000-8000-000000000001";
    public const string SubjectId = "01900000-0000-7000-8000-000000000002";
    public const string OtherTenantId = "01900000-0000-7000-8000-0000000000ff";

    public static string AiUrl(string tenantId = TenantId) => $"/api/v1/tenants/{tenantId}/ai";

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

    /// <summary>Siembra la fila del tenant y sus módulos; con fila, el stub de desarrollo enmascara por módulo como el real.</summary>
    public static async Task SeedTenantAsync(QepApiFactory factory, IEnumerable<TenantModuleKey>? modules = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var id = new Modules.Tenancy.Domain.TenantId(Guid.Parse(TenantId));
        var now = DateTimeOffset.UtcNow;
        if (await tenancy.Tenants.FindAsync([id], TestContext.Current.CancellationToken) is null)
        {
            tenancy.Tenants.Add(Tenant.Create(
                id, "ai-api-tests", "Ai API Tests", "es-CO", "America/Bogota", "yyyy-MM-dd", MembershipId.New(), now));
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

    // El stub concede sólo los defaults de tenancy cuando X-Permissions no está: el permiso de ai se pide explícito, así cada 403 queda
    // atribuible al permiso, al tenant o al módulo.
    public static HttpClient CreateClient(QepApiFactory factory, string tenantId = TenantId, params string[] permissions)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", SubjectId);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId);
        if (permissions.Length > 0)
        {
            client.DefaultRequestHeaders.Add("X-Permissions", string.Join(',', permissions));
        }

        return client;
    }

    public static async Task<(PostgreSqlContainer Database, QepApiFactory Factory)> OpenAsync(IEnumerable<TenantModuleKey>? modules = null)
    {
        var database = await StartDatabaseAsync();
        var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory, modules);
        return (database, factory);
    }

    public static async Task<string> BodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

    /// <param name="configureServices">Sustituciones de servicios para la prueba; corre después del registro de la aplicación.</param>
    public sealed class QepApiFactory(string connectionString, Action<IServiceCollection>? configureServices = null)
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
            // Fijado, nunca heredado de appsettings.json: deja explícito qué canal de correo ejerce la prueba.
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
            // Fijado: los user-secrets del developer pueden traer Seed:Enabled=true.
            builder.UseSetting("Seed:Enabled", "false");

            if (configureServices is not null)
            {
                builder.ConfigureServices(configureServices);
            }
        }
    }
}
