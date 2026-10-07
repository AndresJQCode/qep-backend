using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Modules.Tenancy.IntegrationTests;

/// <summary>
/// Módulos por tenant de punta a punta (spec 2026-10-07): lo que deja el signup, el enmascarado del
/// stub sobre un tenant con fila y el endpoint <c>/modules</c>. Corre con el stub de desarrollo; la
/// cookie real va en <c>RealAuthenticationApiTests</c>.
/// </summary>
public sealed class TenantModulesApiTests
{
    // Es la prueba de que EF ordena el INSERT de tenants antes que el de tenant_modules: los dos
    // salen del mismo SaveChangesAsync de TenantRegistrationService, y sin la FK en el modelo el
    // orden no estaría garantizado (23503).
    [Fact]
    public async Task SignupStoresTheSixDefaultModules()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());

        var (tenantId, _) = await RegisterAsync(factory);

        Assert.Equal(
            [
                ("catalog", "signup"), ("companies", "signup"), ("customers", "signup"),
                ("orders", "signup"), ("quotations", "signup"), ("reporting", "signup"),
            ],
            await RowsAsync(factory, tenantId));
    }

    [Fact]
    public async Task SignupWithTheSwitchOffStoresNoModule()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), grantDefaultModulesOnSignup: false);

        var (tenantId, _) = await RegisterAsync(factory);

        Assert.Empty(await RowsAsync(factory, tenantId));
    }

    internal static async Task<(Guid TenantId, Guid OwnerUserId)> RegisterAsync(QepApiFactory factory)
    {
        using var bootstrap = StubClient(factory, Guid.CreateVersion7(), Guid.CreateVersion7());
        bootstrap.DefaultRequestHeaders.Add("X-Email", $"owner-{Guid.CreateVersion7():N}@example.com");
        bootstrap.DefaultRequestHeaders.Add("X-Email-Verified", "true");

        var response = await bootstrap.PostAsJsonAsync(
            "/api/v1/auth/register-tenant",
            new
            {
                displayName = "Modules Test Org",
                slug = $"mod-{Guid.NewGuid():N}"[..12],
                defaultCulture = "es-CO",
                timeZone = "America/Bogota",
                dateFormat = "yyyy-MM-dd",
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var registered = await response.Content.ReadFromJsonAsync<RegisteredDto>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(registered);
        return (registered.TenantId, registered.OwnerUserId);
    }

    internal static HttpClient StubClient(
        QepApiFactory factory, Guid subjectId, Guid tenantId, params string[] permissions)
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

    internal static async Task DisableAsync(QepApiFactory factory, Guid tenantId, TenantModuleKey key)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var id = new TenantId(tenantId);
        await dbContext.TenantModules
            .Where(module => module.TenantId == id && module.ModuleKey == key)
            .ExecuteDeleteAsync(TestContext.Current.CancellationToken);
    }

    internal static async Task<List<(string Key, string Source)>> RowsAsync(QepApiFactory factory, Guid tenantId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var id = new TenantId(tenantId);
        var rows = await dbContext.TenantModules
            .AsNoTracking()
            .Where(module => module.TenantId == id)
            .ToListAsync(TestContext.Current.CancellationToken);
        return rows
            .Select(module => (module.ModuleKey.Value, module.Source))
            .OrderBy(row => row.Value, StringComparer.Ordinal)
            .ToList();
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

    private sealed record RegisteredDto(Guid TenantId, Guid OwnerUserId);

    internal sealed class QepApiFactory(string connectionString, bool? grantDefaultModulesOnSignup = null)
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
            // Fijados, nunca heredados de appsettings.json ni de los user-secrets de quien corre
            // las pruebas (SDD-CT-17): cualquiera de los dos puede tumbar el arranque.
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
            builder.UseSetting("Registration:PublicTenantSignupEnabled", "true");
            builder.UseSetting("Authentication:UseDevelopmentStub", "true");
            if (grantDefaultModulesOnSignup is { } grant)
            {
                builder.UseSetting("Entitlements:GrantDefaultModulesOnSignup", grant ? "true" : "false");
            }
        }
    }
}
