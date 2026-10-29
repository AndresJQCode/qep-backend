using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Billing.Application;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Billing.IntegrationTests;

/// <summary>
/// El arranque compartido de las pruebas de integración del módulo. Copia el de Suppliers: un
/// tenant con fila (sin ella no hay máscara de módulos ni reloj del tenant) y
/// <c>configureServices</c> en la fábrica.
/// </summary>
internal static class BillingApiHarness
{
    public const string TenantId = "01900000-0000-7000-8000-000000000001";
    public const string SubjectId = "01900000-0000-7000-8000-000000000002";
    public const string OtherTenantId = "01900000-0000-7000-8000-0000000000ff";
    public const string OtherSubjectId = "01900000-0000-7000-8000-0000000000fe";

    public static string BillingUrl(string tenantId = TenantId) =>
        $"/api/v1/tenants/{tenantId}/billing";

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
    /// Siembra la fila del tenant y sus módulos. Inserta únicamente las claves que faltan, por lo
    /// que se puede llamar dos veces sobre el mismo tenant.
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
                "billing-api-tests",
                "Billing API Tests",
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

    /// <summary>
    /// Inserta en <c>customers.customers</c> lo mínimo para que el cliente exista; Billing lo
    /// referencia por id suelto, así que las pruebas no necesitan pasar por el módulo Customers.
    /// </summary>
    public static async Task<Guid> SeedCustomerAsync(
        QepApiFactory factory,
        Guid tenantId,
        string name,
        string? phoneE164 = null,
        string? email = null)
    {
        var id = Guid.CreateVersion7();
        var connectionString = factory.Services
            .GetRequiredService<IConfiguration>()
            .GetConnectionString("QepDatabase")!;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO customers.customers (id, tenant_id, name, is_active, phone_e164, email, completeness, whatsapp_user_id, with_retention, vat_surplus, version, created_at, updated_at)
            VALUES (@id, @tenantId, @name, true, @phone, @email, 'Incomplete', @whatsAppUserId, false, false, 1, now(), now())
            """,
            connection);
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("tenantId", tenantId);
        insert.Parameters.AddWithValue("name", name);
        // 'Incomplete' porque CK_customers_complete_fields (CustomersDbContext.CompleteFieldsCheck) exige CUC, documento,
        // dirección y país a una ficha 'Complete'; Billing sólo necesita nombre, teléfono y correo.
        // Único por llamada: Customers puede exigir un whatsapp_user_id distinto por cliente.
        insert.Parameters.AddWithValue("whatsAppUserId", $"CO.{id:N}");
        insert.Parameters.AddWithValue("phone", (object?)phoneE164 ?? DBNull.Value);
        insert.Parameters.AddWithValue("email", (object?)email ?? DBNull.Value);
        await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return id;
    }

    // El stub concede sólo los defaults de tenancy cuando X-Permissions no está, así que el permiso
    // de billing se pide explícito: cada 403 queda atribuible al permiso o al tenant.
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
        CreateClient(factory, SubjectId, TenantId, BillingPermissions.Read, BillingPermissions.Manage);

    public static HttpClient CreateReader(QepApiFactory factory) =>
        CreateClient(factory, SubjectId, TenantId, BillingPermissions.Read);

    /// <summary>Abre base, fábrica y tenant sembrado: el arranque que repiten todas las pruebas.</summary>
    public static async Task<(PostgreSqlContainer Database, QepApiFactory Factory)> OpenAsync(
        IEnumerable<TenantModuleKey>? modules = null)
    {
        var database = await StartDatabaseAsync();
        var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory, modules: modules);
        return (database, factory);
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
            // Fijado: los user-secrets del developer pueden traer Seed:Enabled=true y sembrar datos
            // en el tenant de pruebas. El harness no depende del entorno del shell.
            builder.UseSetting("Seed:Enabled", "false");

            if (configureServices is not null)
            {
                builder.ConfigureServices(configureServices);
            }
        }
    }
}
