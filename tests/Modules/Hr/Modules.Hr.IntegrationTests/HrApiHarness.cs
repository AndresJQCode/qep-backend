using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Hr.Application;
using Modules.Identity.Domain;
using Modules.Identity.Infrastructure.Persistence;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Hr.IntegrationTests;

/// <summary>
/// El arranque compartido de las pruebas de integración del módulo. Copia el de Loans: un
/// tenant con fila (sin ella no hay máscara de módulos ni reloj del tenant) y
/// <c>configureServices</c> en la fábrica.
/// </summary>
internal static class HrApiHarness
{
    public const string TenantId = "01900000-0000-7000-8000-000000000001";
    public const string SubjectId = "01900000-0000-7000-8000-000000000002";
    public const string OtherTenantId = "01900000-0000-7000-8000-0000000000ff";
    public const string OtherSubjectId = "01900000-0000-7000-8000-0000000000fe";

    public static string HrUrl(string tenantId = TenantId) =>
        $"/api/v1/tenants/{tenantId}/hr";

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
    /// que se puede llamar dos veces sobre el mismo tenant. Sin <paramref name="modules"/> van todas las claves; una lista sin
    /// <c>Companies</c> deja <c>hr</c> apagado en la práctica.
    /// </summary>
    public static async Task SeedTenantAsync(
        QepApiFactory factory,
        string tenantId = TenantId,
        IEnumerable<TenantModuleKey>? modules = null,
        string country = "CO")
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
                // El slug es único en la base: un segundo tenant necesita el suyo.
                tenantId == TenantId ? "hr-api-tests" : $"hr-api-{tenantId[^2..]}",
                "Hr API Tests",
                "es-CO",
                "America/Bogota",
                "yyyy-MM-dd",
                MembershipId.New(),
                now));
            await tenancy.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // El país por defecto del empleado sale del tenant: las pruebas lo fijan sin pasar por la pantalla de configuración
        // (precedente CustomersApiHarness).
        await tenancy.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE tenancy.tenants SET country = {country} WHERE id = {id.Value}",
            TestContext.Current.CancellationToken);

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
    /// Siembra una membresía del tenant (con su usuario) con un rol de fábrica: <c>admin</c> concede los cinco
    /// permisos <c>hr.*</c>; <c>advisor</c> y <c>billing</c> no. <paramref name="removed"/> la deja quitada.
    /// </summary>
    public static async Task<Guid> SeedMemberAsync(
        QepApiFactory factory,
        string email,
        string? displayName,
        string role,
        bool removed = false,
        string tenantId = TenantId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var identity = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var ct = TestContext.Current.CancellationToken;
        var tenant = await tenancy.Tenants.FindAsync([new Modules.Tenancy.Domain.TenantId(Guid.Parse(tenantId))], ct);
        var now = DateTimeOffset.UtcNow;

        var user = User.CreateInvited(UserId.New(), email, now);
        identity.Users.Add(user);
        var membership = Membership.CreateActive(
            MembershipId.New(), user.Id.Value, tenant!.Id, [role], "test", now);
        if (displayName is not null)
        {
            membership.UpdateProfile(displayName, null, now);
        }

        if (removed)
        {
            membership.Remove(tenant, now);
        }

        tenancy.Memberships.Add(membership);
        await identity.SaveChangesAsync(ct);
        await tenancy.SaveChangesAsync(ct);
        return membership.Id.Value;
    }

    /// <summary>
    /// La membresía activa de quien llama (<see cref="SubjectId"/>) en el tenant: la que marca una cuota como pagada y la que
    /// resuelve <c>assignee=me</c>. Sin ella el stub sólo trae permisos y el handler responde 403.
    /// </summary>
    public static async Task<Guid> SeedCallerMembershipAsync(
        QepApiFactory factory, string displayName, string role = "admin", string tenantId = TenantId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var identity = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;

        // One user per database: a class that seeds one tenant per test in a shared database calls this once per tenant.
        var userId = UserId.Parse(SubjectId);
        if (await identity.Users.FindAsync([userId], ct) is null)
        {
            identity.Users.Add(User.CreateInvited(userId, $"caller-{tenantId[^2..]}@example.com", now));
        }

        var membership = Membership.CreateActive(
            MembershipId.New(), userId.Value, new Modules.Tenancy.Domain.TenantId(Guid.Parse(tenantId)), [role], "test", now);
        membership.UpdateProfile(displayName, null, now);
        tenancy.Memberships.Add(membership);
        await identity.SaveChangesAsync(ct);
        await tenancy.SaveChangesAsync(ct);
        return membership.Id.Value;
    }

    /// <summary>La cantidad de filas de <c>audit.entries</c> de una acción sobre un recurso: la auditoría atómica no tiene API de lectura acá.</summary>
    public static async Task<int> CountAuditEntriesAsync(QepApiFactory factory, string action, string resourceId)
    {
        var connectionString = factory.Services
            .GetRequiredService<IConfiguration>()
            .GetConnectionString("QepDatabase")!;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var count = new NpgsqlCommand(
            "SELECT count(*) FROM audit.entries WHERE action = @action AND resource_id = @resourceId",
            connection);
        count.Parameters.AddWithValue("action", action);
        count.Parameters.AddWithValue("resourceId", resourceId);
        return (int)(long)(await count.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    // El stub concede sólo los defaults de tenancy cuando X-Permissions no está, así que el permiso
    // de hr se pide explícito: cada 403 queda atribuible al permiso o al tenant.
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

    /// <summary>Los siete permisos <c>hr.*</c>: el cliente que puede todo en el módulo.</summary>
    public static HttpClient CreateFullAccess(QepApiFactory factory) =>
        CreateClient(
            factory, SubjectId, TenantId,
            HrPermissions.EmployeeRead, HrPermissions.EmployeeManage,
            HrPermissions.ContractRead, HrPermissions.ContractManage,
            HrPermissions.SettingsManage, HrPermissions.MedicalRead, HrPermissions.MedicalManage);

    /// <summary>Abre base, fábrica y tenant sembrado: el arranque que repiten todas las pruebas.</summary>
    public static async Task<(PostgreSqlContainer Database, QepApiFactory Factory)> OpenAsync(
        IEnumerable<TenantModuleKey>? modules = null)
    {
        var database = await StartDatabaseAsync();
        var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory, modules: modules);
        return (database, factory);
    }

    internal sealed record CompanyRef(Guid Id, string Name, string TaxId);

    private sealed record GeographyDepartmentDto(Guid Id, string DivipolaCode, string Name);

    private sealed record GeographyCityDto(Guid Id, string DivipolaCode, string Name, Guid DepartmentId);

    private sealed record IdDto(Guid Id);

    /// <summary>
    /// Crea una empresa real por <c>POST /companies</c> (calco de <c>PosApiHarness.CreateCompanyAsync</c>): las pruebas de Hr
    /// siembran empresas de verdad, así el adaptador de <c>IHrCompanyLookup</c> queda cubierto. <paramref name="client"/> necesita
    /// <c>companies.company.manage</c>.
    /// </summary>
    public static async Task<CompanyRef> CreateCompanyAsync(
        HttpClient client, Guid tenantId, string name = "Comercializadora QEP S.A.S.")
    {
        var cityId = await EnsureCityIdAsync(client);
        var taxId = $"901.{Random.Shared.Next(100, 999)}.{Random.Shared.Next(100, 999)}-2";
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/companies",
            new
            {
                name,
                bankAccounts = new[]
                {
                    new { bankName = "Bancolombia", accountNumber = $"{Random.Shared.Next(100000000, 999999999)}", currency = "COP" },
                },
                chargesVat = true,
                taxId,
                country = "CO",
                documentType = "NIT",
                cityId,
                phone = "6045551234",
                email = "rrhh@qep.example.co",
                address = "Cra 50 # 10-20, Rionegro",
            },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<IdDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return new CompanyRef(body.Id, name, taxId);
    }

    /// <summary>Una empresa de Estados Unidos (documento <c>TAX_ID</c>, ciudad por nombre): país distinto de Colombia.</summary>
    public static async Task<CompanyRef> CreateForeignCompanyAsync(
        HttpClient client, Guid tenantId, string name = "Andes Inc.")
    {
        var taxId = $"{Random.Shared.Next(10, 99)}-{Random.Shared.Next(1000000, 9999999)}";
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/companies",
            new
            {
                name,
                bankAccounts = new[] { new { bankName = "Chase", accountNumber = $"{Random.Shared.Next(100000000, 999999999)}", currency = "USD" } },
                chargesVat = false,
                taxId,
                country = "US",
                documentType = "TAX_ID",
                cityName = "Miami",
            },
            TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var body = await response.Content.ReadFromJsonAsync<IdDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return new CompanyRef(body.Id, name, taxId);
    }

    /// <summary>Desactiva la empresa por <c>POST /companies/{id}/deactivate</c>.</summary>
    public static async Task DeactivateCompanyAsync(HttpClient client, Guid tenantId, Guid companyId)
    {
        var response = await client.PostAsync(
            $"/api/v1/tenants/{tenantId}/companies/{companyId}/deactivate", null, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<Guid> EnsureCityIdAsync(HttpClient client)
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

    public static async Task<string> BodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

    /// <param name="configureServices">
    /// Sustituciones de servicios para la prueba; corre después del registro de la aplicación.
    /// </param>
    /// <param name="operatorTenantId">
    /// El tenant que hace de operador de la plataforma (<c>Platform:OperatorTenantId</c>); sin él ninguno lo es.
    /// </param>
    public sealed class QepApiFactory(
        string connectionString, Action<IServiceCollection>? configureServices = null, Guid? operatorTenantId = null)
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
            if (operatorTenantId is { } operatorTenant)
            {
                builder.UseSetting("Platform:OperatorTenantId", operatorTenant.ToString());
            }

            if (configureServices is not null)
            {
                builder.ConfigureServices(configureServices);
            }
        }
    }
}

/// <summary>Reloj del tenant movible: las pruebas fijan «hoy» y lo adelantan sin esperar al calendario real.</summary>
internal sealed class MutableTenantClock(DateTimeOffset utcNow) : ITenantClock
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;

    public Task<TenantCalendar> GetAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(new TenantCalendar(UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Bogota")));
}
