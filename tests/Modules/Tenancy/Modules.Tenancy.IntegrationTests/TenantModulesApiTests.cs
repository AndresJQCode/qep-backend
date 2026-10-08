using System.Net;
using System.Net.Http.Json;
using Api;
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

    // Criterio de "Cuándo el stub enmascara": con fila en tenancy.tenants, enmascara.
    [Fact]
    public async Task TheStubMasksARegisteredTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerId) = await RegisterAsync(factory);
        using var client = StubClient(factory, ownerId, tenantId, "catalog.product.read", "tenancy.settings.read");
        Assert.Contains("catalog.product.read", await EffectivePermissionsAsync(client, tenantId));

        await DisableAsync(factory, tenantId, TenantModuleKeys.Catalog);

        var permissions = await EffectivePermissionsAsync(client, tenantId);
        Assert.DoesNotContain("catalog.product.read", permissions);
        Assert.Contains("tenancy.settings.read", permissions);
    }

    [Fact]
    public async Task WithTheSwitchOffTheOwnerOnlyKeepsCorePermissions()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), grantDefaultModulesOnSignup: false);
        var (tenantId, ownerId) = await RegisterAsync(factory);
        using var client = StubClient(
            factory, ownerId, tenantId, "tenancy.settings.read", "catalog.product.read", "quotations.quotation.read");

        Assert.Equal(["tenancy.settings.read"], await EffectivePermissionsAsync(client, tenantId));
    }

    // Sin fila (tenant simulado): no enmascara, para no romper las suites que nunca registran tenant.
    [Fact]
    public async Task TheStubLeavesASimulatedTenantAlone()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        using var client = StubClient(factory, Guid.CreateVersion7(), tenantId, "catalog.product.read");

        Assert.Equal(["catalog.product.read"], await EffectivePermissionsAsync(client, tenantId));
    }

    [Fact]
    public async Task ModulesListsTheSevenInOrder()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerId) = await RegisterAsync(factory);
        using var client = StubClient(factory, ownerId, tenantId);

        var modules = await ModulesAsync(client, tenantId);

        Assert.Equal(tenantId, modules.TenantId);
        Assert.Equal(
            ["catalog", "customers", "companies", "quotations", "orders", "reporting", "pos"],
            modules.Modules.Select(module => module.Key));
        Assert.All(modules.Modules.Where(module => module.Key != "pos"), module =>
        {
            Assert.True(module.Enabled);
            Assert.True(module.Contracted);
            Assert.Empty(module.MissingDependencies);
        });
        var pos = modules.Modules.Single(module => module.Key == "pos");
        Assert.False(pos.Enabled);
        Assert.False(pos.Contracted);
    }

    [Fact]
    public async Task TurningCustomersOffNamesItAsTheRootCause()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerId) = await RegisterAsync(factory);
        using var client = StubClient(factory, ownerId, tenantId);

        await DisableAsync(factory, tenantId, TenantModuleKeys.Customers);

        var modules = (await ModulesAsync(client, tenantId)).Modules.ToDictionary(module => module.Key);
        Assert.False(modules["customers"].Contracted);
        Assert.Empty(modules["customers"].MissingDependencies);
        foreach (var key in new[] { "quotations", "orders" })
        {
            Assert.False(modules[key].Enabled);
            Assert.True(modules[key].Contracted);
            Assert.Equal(["customers"], modules[key].MissingDependencies);
        }
    }

    [Fact]
    public async Task ASimulatedTenantSeesEverythingOn()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        using var client = StubClient(factory, Guid.CreateVersion7(), tenantId);

        var modules = await ModulesAsync(client, tenantId);

        Assert.Equal(7, modules.Modules.Count);
        Assert.All(modules.Modules, module =>
        {
            Assert.True(module.Enabled);
            Assert.True(module.Contracted);
        });
    }

    [Fact]
    public async Task ModulesOfAnotherTenantAreForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerId) = await RegisterAsync(factory);
        using var client = StubClient(factory, ownerId, tenantId);

        var response = await client.GetAsync(
            $"/api/v1/tenants/{Guid.CreateVersion7()}/modules", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // Sin HTTP: por cookie real, null exige un tenant sin fila con una membresía activa, que no se
    // puede armar por la API.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WithoutARowTheAnswerDependsOnTheScheme(bool isDevelopmentStub)
    {
        var tenantId = Guid.CreateVersion7();

        var response = TenantModulesResponse.From(tenantId, modules: null, isDevelopmentStub);

        Assert.Equal(7, response.Modules.Count);
        Assert.All(response.Modules, module =>
        {
            Assert.Equal(isDevelopmentStub, module.Enabled);
            Assert.Equal(isDevelopmentStub, module.Contracted);
            Assert.Empty(module.MissingDependencies);
        });
    }

    private static async Task<ModulesDto> ModulesAsync(HttpClient client, Guid tenantId)
    {
        var response = await client.GetAsync(
            $"/api/v1/tenants/{tenantId}/modules", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var modules = await response.Content.ReadFromJsonAsync<ModulesDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(modules);
        return modules;
    }

    private sealed record ModulesDto(Guid TenantId, List<ModuleDto> Modules);

    private sealed record ModuleDto(string Key, bool Enabled, bool Contracted, string[] MissingDependencies);

    internal static async Task<string[]> EffectivePermissionsAsync(HttpClient client, Guid tenantId)
    {
        var response = await client.GetFromJsonAsync<EffectivePermissionsDto>(
            $"/api/v1/tenants/{tenantId}/authorization/me", TestContext.Current.CancellationToken);
        Assert.NotNull(response);
        return response.Permissions;
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

    // Spec 2026-10-08 §3: apagar deja la fila inactiva; contratado = fila activa.
    internal static async Task DisableAsync(QepApiFactory factory, Guid tenantId, TenantModuleKey key)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var id = new TenantId(tenantId);
        var now = DateTimeOffset.UtcNow;
        await dbContext.TenantModules
            .Where(module => module.TenantId == id && module.ModuleKey == key)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(module => module.Status, TenantModuleStatus.Inactive)
                    .SetProperty(module => module.StatusChangedAt, now),
                TestContext.Current.CancellationToken);
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

    private sealed record EffectivePermissionsDto(Guid TenantId, string[] Permissions);

    internal sealed class QepApiFactory(
        string connectionString, bool? grantDefaultModulesOnSignup = null, Guid? operatorTenantId = null)
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

            // Siempre fijado, aunque la prueba no pida operador: un Platform:OperatorTenantId en los
            // user-secrets de quien corre las pruebas convertiría en operador a un tenant ajeno.
            // Vacío vale lo mismo que ausente (OperatorTenantOptionsTests).
            builder.UseSetting("Platform:OperatorTenantId", operatorTenantId?.ToString() ?? string.Empty);
        }
    }
}
