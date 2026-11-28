using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;
using Modules.Wms.Application;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Wms.IntegrationTests;

/// <summary>
/// El arranque compartido de las pruebas de integración de Bodegas, copia del de Loans: un tenant con fila (sin ella no hay máscara
/// de módulos ni reloj del tenant) y <c>configureServices</c> en la fábrica. Es <c>partial</c> a propósito: el plan WMS-D agrega
/// <c>WmsApiHarness.Inventory.cs</c> con sus helpers sin tocar este archivo.
/// </summary>
internal static partial class WmsApiHarness
{
    public const string TenantId = "01900000-0000-7000-8000-000000000001";
    public const string SubjectId = "01900000-0000-7000-8000-000000000002";
    public const string OtherTenantId = "01900000-0000-7000-8000-0000000000ff";
    public const string OtherSubjectId = "01900000-0000-7000-8000-0000000000fe";

    /// <summary>Las diecisiete constantes de <see cref="WmsPermissions"/>: quien administra el módulo en las pruebas.</summary>
    public static readonly string[] AllPermissions =
    [
        WmsPermissions.WarehouseManage,
        WmsPermissions.OwnerManage,
        WmsPermissions.ProfileManage,
        WmsPermissions.DocumentRead,
        WmsPermissions.StockRead,
        WmsPermissions.StockExport,
        WmsPermissions.AdjustmentPropose,
        WmsPermissions.AdjustmentApprove,
        WmsPermissions.PurchaseOrderManage,
        WmsPermissions.TransferManage,
        WmsPermissions.TransferDispatch,
        WmsPermissions.ReceiptPerform,
        WmsPermissions.PutawayPerform,
        WmsPermissions.DiscrepancyResolve,
        WmsPermissions.CountPerform,
        WmsPermissions.CountApprove,
        WmsPermissions.TaskManage,
    ];

    /// <summary>La marca de arranque del consumidor de inventario básico en las pruebas: lo anterior no se mira.</summary>
    public static readonly DateTimeOffset InventoryStartAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static string WmsUrl(string tenantId = TenantId) => $"/api/v1/tenants/{tenantId}/wms";

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

    /// <summary>Siembra la fila del tenant y sus módulos. Inserta únicamente las claves que faltan, por lo que se puede llamar dos
    /// veces sobre el mismo tenant.</summary>
    public static async Task SeedTenantAsync(
        QepApiFactory factory,
        string tenantId = TenantId,
        IEnumerable<TenantModuleKey>? modules = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var id = new Modules.Tenancy.Domain.TenantId(Guid.Parse(tenantId));
        var now = DateTimeOffset.UtcNow;
        var existing = await tenancy.Tenants.FindAsync([id], TestContext.Current.CancellationToken);
        if (existing is null)
        {
            tenancy.Tenants.Add(Tenant.Create(
                id,
                // El slug es único en la base: un segundo tenant necesita el suyo.
                tenantId == TenantId ? "wms-api-tests" : $"wms-api-{tenantId[^2..]}",
                "Wms API Tests",
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

    // El stub concede sólo los defaults de tenancy cuando X-Permissions no está, así que el permiso de wms se pide explícito: cada
    // 403 queda atribuible al permiso o al tenant.
    public static HttpClient CreateClient(QepApiFactory factory, string subjectId, string tenantId, params string[] permissions)
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
        CreateClient(factory, SubjectId, TenantId, AllPermissions);

    public static HttpClient CreateReader(QepApiFactory factory) =>
        CreateClient(factory, SubjectId, TenantId, WmsPermissions.DocumentRead);

    /// <summary>Abre base, fábrica y tenant sembrado: el arranque que repiten todas las pruebas.</summary>
    public static async Task<(PostgreSqlContainer Database, QepApiFactory Factory)> OpenAsync(
        IEnumerable<TenantModuleKey>? modules = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var database = await StartDatabaseAsync();
        var factory = new QepApiFactory(database.GetConnectionString(), configureServices);
        await SeedTenantAsync(factory, modules: modules);
        return (database, factory);
    }

    public static async Task<string> BodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

    /// <summary>El <c>code</c> del cuerpo de error (<c>authorization.denied</c>, <c>wms.…</c>); null si no trae.</summary>
    public static async Task<string?> CodeOfAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await BodyAsync(response));
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    /// <summary>La cantidad de filas de <c>audit.entries</c> de una acción sobre un recurso: la auditoría atómica no tiene API de
    /// lectura acá.</summary>
    public static async Task<int> CountAuditEntriesAsync(QepApiFactory factory, string action, string resourceId)
    {
        var connectionString = factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("QepDatabase")!;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var count = new NpgsqlCommand(
            "SELECT count(*) FROM audit.entries WHERE action = @action AND resource_id = @resourceId",
            connection);
        count.Parameters.AddWithValue("action", action);
        count.Parameters.AddWithValue("resourceId", resourceId);
        return (int)(long)(await count.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    /// <summary>Un producto por la API de Catalog con una variante con código, código de barras y empaques: lo que el perfil
    /// logístico copia y siembra. Usa un cliente propio con <c>catalog.product.manage</c>. Devuelve el id de la variante, que es el
    /// del producto: la variante por defecto comparte el Guid (<c>VariantId.cs</c>).</summary>
    public static async Task<Guid> CreateVariantAsync(
        QepApiFactory factory, string code, string? barcode, int[] packagingUnits, string name = "Producto", string tenantId = TenantId)
    {
        using var client = CreateClient(factory, SubjectId, tenantId, "catalog.product.manage");
        // `barcode` va en la variante junto a `code` (ProductVariantRequest.Barcode) y `packagingUnits` va DENTRO de `pricing`
        // (ProductPricingRequest.PackagingUnits).
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/products",
            new
            {
                name,
                variants = new[]
                {
                    new
                    {
                        code,
                        barcode,
                        pricing = new
                        {
                            prices = new Dictionary<string, decimal> { ["COP"] = 10_000m },
                            scales = new object[]
                            {
                                new { fromUnit = 1, toUnit = 999_999, discount = 0m, restriction = "multiple", multiple = 1 },
                            },
                            packagingUnits,
                        },
                    },
                },
            },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<IdDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body.Id;
    }

    /// <summary>Una ciudad real: <c>GeographySeeder</c> siembra el DIVIPOLA en cada arranque. Copia de
    /// <c>CompaniesApiHarness.EnsureCityIdAsync</c>.</summary>
    public static async Task<Guid> AnyCityIdAsync(QepApiFactory factory)
    {
        using var client = CreateClient(factory, SubjectId, TenantId);
        var departments = await client.GetFromJsonAsync<List<GeographyDepartmentDto>>(
            "/api/v1/departments", TestContext.Current.CancellationToken);
        Assert.NotNull(departments);
        Assert.NotEmpty(departments);

        foreach (var department in departments)
        {
            var cities = await client.GetFromJsonAsync<List<GeographyCityDto>>(
                $"/api/v1/cities?departmentId={department.Id}", TestContext.Current.CancellationToken);
            if (cities is { Count: > 0 })
            {
                return cities[0].Id;
            }
        }

        throw new InvalidOperationException("No seeded DIVIPOLA department has at least one city.");
    }

    private sealed record IdDto(Guid Id);

    private sealed record GeographyDepartmentDto(Guid Id, string DivipolaCode, string Name);

    private sealed record GeographyCityDto(Guid Id, string DivipolaCode, string Name, Guid DepartmentId);

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
            // Fijado, nunca heredado de appsettings.json: con "infobip" y las claves ausentes el validador de Notifications falla
            // al arrancar. SDD-CT-17.
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
            // Fijado: los user-secrets del developer pueden traer Seed:Enabled=true y sembrar datos en el tenant de pruebas.
            builder.UseSetting("Seed:Enabled", "false");
            // Ningún worker de WMS corre solo en una prueba: cada prueba de worker llama a mano su RunOnceAsync/PublishOnceAsync.
            builder.UseSetting("Wms:Workers:WorkerStartupSeconds", "3600");
            builder.UseSetting("Wms:Workers:BalanceChangePublishSeconds", "3600");
            builder.UseSetting("Wms:Workers:ReconciliationSweepMinutes", "60");
            builder.UseSetting("Wms:Workers:PartitionSweepHours", "24");
            builder.UseSetting("Wms:Workers:HousekeepingHours", "24");
            builder.UseSetting("Wms:Workers:HousekeepingStartupSeconds", "3600");
            builder.UseSetting("Wms:InventoryEvents:StartAt", InventoryStartAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));

            if (configureServices is not null)
            {
                builder.ConfigureServices(configureServices);
            }
        }
    }
}
