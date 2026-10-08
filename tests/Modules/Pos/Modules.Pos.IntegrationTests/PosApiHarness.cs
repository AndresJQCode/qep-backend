using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Modules.Catalog.Application;
using Modules.Companies.Application;
using Modules.Pos.Application;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Pos.IntegrationTests;

/// <summary>
/// Harness del módulo Pos (spec 2026-10-07, «Integración»). Registra un tenant real —el cajero se
/// resuelve a su membresía por IMembershipDirectory, que lee membresías de verdad— y prende `pos`
/// por SQL: ni el signup ni ninguna configuración lo prenden, y sin la fila todo /pos/* da 403
/// aunque el header pida los permisos.
/// </summary>
internal static class PosApiHarness
{
    public static string PosUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/pos";

    public static readonly string[] CashierPermissions =
    [
        PosPermissions.SaleRead,
        PosPermissions.SaleCreate,
        PosPermissions.RegisterOperate,
    ];

    public static readonly string[] AdminPosPermissions =
    [
        .. CashierPermissions,
        PosPermissions.SaleVoid,
        PosPermissions.SaleDiscount,
        PosPermissions.RegisterRead,
    ];

    /// <summary>Lo que hace falta para sembrar empresa, tasas y productos por sus APIs.</summary>
    public static readonly string[] SeedPermissions =
    [
        CatalogPermissions.ProductRead,
        CatalogPermissions.ProductManage,
        CatalogPermissions.TaxRateRead,
        CatalogPermissions.TaxRateManage,
        CompaniesPermissions.CompanyRead,
        CompaniesPermissions.CompanyManage,
    ];

    private static readonly string[] CashierRole = ["cashier"];

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

    public static HttpClient CreateClient(
        QepApiFactory factory,
        Guid subjectId,
        Guid tenantId,
        params string[] permissions)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", subjectId.ToString());
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId.ToString());
        if (permissions.Length > 0)
        {
            client.DefaultRequestHeaders.Add("X-Permissions", string.Join(',', permissions));
        }

        return client;
    }

    internal sealed record PosTenant(Guid TenantId, Guid OwnerUserId, HttpClient Seeder);

    /// <summary>Copia de QuotationsApiHarness.RegisterTenantInTimeZoneAsync, en Bogotá.</summary>
    public static async Task<PosTenant> RegisterTenantAsync(QepApiFactory factory)
    {
        var email = $"owner-{Guid.CreateVersion7():N}@example.com";
        using var bootstrap = CreateClient(factory, Guid.CreateVersion7(), Guid.CreateVersion7());
        bootstrap.DefaultRequestHeaders.Add("X-Email", email);
        bootstrap.DefaultRequestHeaders.Add("X-Email-Verified", "true");

        var response = await bootstrap.PostAsJsonAsync(
            "/api/v1/auth/register-tenant",
            new
            {
                displayName = "Pos Test Org",
                slug = $"org-{Guid.NewGuid():N}"[..12],
                defaultCulture = "es-CO",
                timeZone = "America/Bogota",
                dateFormat = "yyyy-MM-dd",
            },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var registered = await response.Content.ReadFromJsonAsync<RegisterTenantResponseDto>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(registered);

        return new PosTenant(
            registered.TenantId,
            registered.OwnerUserId,
            CreateClient(factory, registered.OwnerUserId, registered.TenantId, SeedPermissions));
    }

    public static Task EnablePosAsync(PostgreSqlContainer database, Guid tenantId) =>
        ExecuteSqlAsync(
            database,
            """
            INSERT INTO tenancy.tenant_modules (tenant_id, module_key, enabled_at, source)
            VALUES (@tenantId, 'pos', now(), 'manual')
            ON CONFLICT (tenant_id, module_key) DO UPDATE SET status = 'active', status_changed_at = now()
            """,
            ("tenantId", tenantId));

    // Spec 2026-10-08 §3: apagar ya no borra la fila, la deja inactiva (el SQL de respaldo del README).
    public static Task DisablePosAsync(PostgreSqlContainer database, Guid tenantId) =>
        ExecuteSqlAsync(
            database,
            "UPDATE tenancy.tenant_modules SET status = 'inactive', status_changed_at = now() WHERE tenant_id = @tenantId AND module_key = 'pos'",
            ("tenantId", tenantId));

    public static async Task ExecuteSqlAsync(
        PostgreSqlContainer database, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    public static async Task<long> CountAsync(
        PostgreSqlContainer database, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(TestContext.Current.CancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Filas de auditoría del POS con esa acción en el outbox de plataforma.</summary>
    public static Task<long> AuditCountAsync(PostgreSqlContainer database, Guid tenantId, string action) =>
        CountAsync(
            database,
            """
            SELECT count(*) FROM platform.outbox_messages
            WHERE event_name = 'platform.audit.recorded.v1'
              AND payload->>'source' = 'pos'
              AND payload->>'action' = @action
              AND payload->>'tenantId' = @tenantId
            """,
            ("action", action),
            ("tenantId", tenantId.ToString()));

    public static async Task<Guid> EnsureCityIdAsync(HttpClient client)
    {
        var departments = await client.GetFromJsonAsync<List<GeographyDepartmentDto>>(
            "/api/v1/departments", TestContext.Current.CancellationToken);
        Assert.NotNull(departments);

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

    internal sealed record CompanyRef(Guid Id, string Name, string TaxId);

    public static async Task<CompanyRef> CreateCompanyAsync(HttpClient client, Guid tenantId)
    {
        var cityId = await EnsureCityIdAsync(client);
        var taxId = $"901.{Random.Shared.Next(100, 999)}.{Random.Shared.Next(100, 999)}-2";
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/companies",
            new
            {
                name = "Origen Botánico SAS",
                bankAccounts = new[]
                {
                    new { bankName = "Bancolombia", accountNumber = $"{Random.Shared.Next(100000000, 999999999)}", currency = "COP" },
                },
                taxId,
                cityId,
                phone = "6045551234",
                email = "caja@origen.example.co",
                address = "Cra 50 # 10-20, Rionegro",
            },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<IdDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return new CompanyRef(body.Id, "Origen Botánico SAS", taxId);
    }

    public static async Task<Guid> CreateTaxRateAsync(HttpClient client, Guid tenantId, string name, int percentage)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/tax-rates",
            new { name, percentage },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<IdDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body.Id;
    }

    /// <summary>Producto con precio COP de lista y las escalas por defecto de QuotationsApiHarness.</summary>
    public static async Task<Guid> CreateProductAsync(
        HttpClient client, Guid tenantId, string code, string name, decimal priceCop, Guid? taxRateId)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/products",
            new
            {
                name,
                code,
                taxRateId,
                pricing = new
                {
                    baseCop = priceCop,
                    scales = new object[]
                    {
                        new { fromUnit = 1, toUnit = 9, discount = 0m, restriction = "multiple", multiple = 1, finalCop = priceCop },
                        new { fromUnit = 10, toUnit = 19, discount = 5m, restriction = "multiple", multiple = 1, finalCop = priceCop * 0.95m },
                        new { fromUnit = 20, toUnit = 999_999, discount = 10m, restriction = "multiple", multiple = 1, finalCop = priceCop * 0.90m },
                    },
                    packagingUnits = Array.Empty<int>(),
                },
            },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<IdDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body.Id;
    }

    internal sealed record InvitedCashier(Guid MembershipId, Guid UserId, HttpClient Client);

    /// <summary>Copia de ReportingApiHarness.InviteActiveAdvisorAsync con el rol `cashier`.</summary>
    public static async Task<InvitedCashier> InviteCashierAsync(
        QepApiFactory factory, PostgreSqlContainer database, PosTenant tenant, params string[] permissions)
    {
        // Sin X-Permissions el stub concede los de tenancy, que traen advisorship.invite.
        using var owner = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId);
        var email = $"cashier-{Guid.CreateVersion7():N}@example.com";
        var invited = await owner.PostAsJsonAsync(
            $"/api/v1/tenants/{tenant.TenantId}/memberships",
            new { email, displayName = "Cajero Invitado", roles = CashierRole },
            TestContext.Current.CancellationToken);
        invited.EnsureSuccessStatusCode();
        var membership = await invited.Content.ReadFromJsonAsync<InvitedMembershipDto>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(membership);

        var token = await FindInvitationTokenAsync(database, membership.Id);
        using var accepting = CreateClient(factory, membership.UserId, tenant.TenantId);
        var accepted = await accepting.PostAsync(
            $"/api/v1/invitations/{token}/accept", content: null, TestContext.Current.CancellationToken);
        accepted.EnsureSuccessStatusCode();

        return new InvitedCashier(
            membership.Id,
            membership.UserId,
            CreateClient(factory, membership.UserId, tenant.TenantId, permissions));
    }

    private static async Task<string> FindInvitationTokenAsync(PostgreSqlContainer database, Guid membershipId)
    {
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT payload::text FROM platform.outbox_messages
            WHERE event_name = 'tenancy.membership-invited.v1'
              AND payload::text LIKE '%' || @membershipId || '%'
            ORDER BY occurred_at DESC
            LIMIT 1
            """,
            connection);
        command.Parameters.AddWithValue("membershipId", membershipId.ToString());
        var payload = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken) as string;
        Assert.NotNull(payload);

        using var document = JsonDocument.Parse(payload);
        var token = document.RootElement.GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));
        return token;
    }

    public sealed class QepApiFactory(string connectionString) : WebApplicationFactory<Program>
    {
        /// <summary>Ajuste opcional del contenedor para una prueba (p. ej. un doble con compuerta).</summary>
        public Action<IServiceCollection>? ConfigureTestServices { get; init; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            if (ConfigureTestServices is { } configure)
            {
                builder.ConfigureTestServices(configure);
            }

            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
            // Fijados, nunca heredados de appsettings ni de los user-secrets de quien corre las
            // pruebas (mismo criterio que CompaniesApiHarness y QuotationsApiHarness).
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
            builder.UseSetting("Seed:ExportLoad:Quotations", "0");
            builder.UseSetting("Quotations:WhatsApp:ApiToken", string.Empty);
            builder.UseSetting("Quotations:WhatsApp:FromNumber", string.Empty);
            builder.UseSetting("Quotations:WhatsApp:TemplateId", string.Empty);
            // Hermético (preflight F-19): el signup concede catalog y companies, de los que
            // depende `pos`; `pos` mismo se prende por SQL en EnablePosAsync.
            builder.UseSetting("Entitlements:GrantDefaultModulesOnSignup", "true");
        }
    }

    private sealed record RegisterTenantResponseDto(Guid TenantId, Guid OwnerUserId);

    private sealed record InvitedMembershipDto(Guid Id, Guid UserId);

    private sealed record GeographyDepartmentDto(Guid Id, string DivipolaCode, string Name);

    private sealed record GeographyCityDto(Guid Id, string DivipolaCode, string Name, Guid DepartmentId);

    private sealed record IdDto(Guid Id);
}
