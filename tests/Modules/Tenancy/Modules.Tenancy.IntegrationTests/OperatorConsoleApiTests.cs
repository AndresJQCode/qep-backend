using System.Net;
using System.Net.Http.Json;
using Testcontainers.PostgreSql;
using static Modules.Tenancy.IntegrationTests.TenantModulesApiTests;

namespace Modules.Tenancy.IntegrationTests;

/// <summary>
/// La consola de operador de punta a punta (spec 2026-10-08) con el stub de desarrollo. El tenant
/// operador es simulado (sin fila en tenancy.tenants): el stub no le enmascara módulos y el filtro
/// de operador lo reconoce por configuración. Los tenants administrados sí se registran por la API.
/// </summary>
public sealed class OperatorConsoleApiTests
{
    private static readonly Guid OperatorTenantId = Guid.Parse("01900000-0000-7000-8000-00000000c0de");

    private static readonly string[] AllOperatorPermissions =
        ["operator.tenants.read", "operator.modules.manage", "operator.tenants.manage"];

    // CA1861: el cuerpo del rol personalizado va en un campo, no como matriz literal.
    private static readonly string[] OnlyTenantsRead = ["operator.tenants.read"];

    [Fact]
    public async Task TheStubKeepsOperatorPermissionsOnlyInTheOperatorTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        var otherTenantId = Guid.CreateVersion7();
        using var inOperator = StubClient(
            factory, Guid.CreateVersion7(), OperatorTenantId, "operator.tenants.read", "tenancy.settings.read");
        using var elsewhere = StubClient(
            factory, Guid.CreateVersion7(), otherTenantId, "operator.tenants.read", "tenancy.settings.read");

        Assert.Contains("operator.tenants.read", await EffectivePermissionsAsync(inOperator, OperatorTenantId));
        Assert.Equal(["tenancy.settings.read"], await EffectivePermissionsAsync(elsewhere, otherTenantId));
    }

    // Spec «Errores y casos borde»: sin la clave, nadie es operador.
    [Fact]
    public async Task WithoutAnOperatorConfiguredTheStubDropsThemEverywhere()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = StubClient(factory, Guid.CreateVersion7(), OperatorTenantId, AllOperatorPermissions);

        Assert.Empty(await EffectivePermissionsAsync(client, OperatorTenantId));
    }

    // Spec 2026-10-08 §2 + D11 (decisión P1 del plan): permissions nunca trae operator.*; roles[]
    // lo oculta fuera del tenant operador.
    [Fact]
    public async Task TheCatalogNeverOffersOperatorCheckboxesAndHidesThemFromOtherAdmins()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        var otherTenantId = Guid.CreateVersion7();

        var inOperator = await CatalogAsync(factory, OperatorTenantId);
        var elsewhere = await CatalogAsync(factory, otherTenantId);

        Assert.DoesNotContain(inOperator.Permissions, p => p.Permission.StartsWith("operator.", StringComparison.Ordinal));
        Assert.DoesNotContain(elsewhere.Permissions, p => p.Permission.StartsWith("operator.", StringComparison.Ordinal));
        Assert.Contains("operator.tenants.read", inOperator.Roles.Single(r => r.Role == "admin").Permissions);
        Assert.DoesNotContain(elsewhere.Roles.Single(r => r.Role == "admin").Permissions,
            p => p.StartsWith("operator.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheRolesListHidesOperatorPermissionsOutsideTheOperatorTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        var otherTenantId = Guid.CreateVersion7();

        Assert.Contains("operator.modules.manage", (await AdminRoleAsync(factory, OperatorTenantId)).Permissions);
        Assert.DoesNotContain((await AdminRoleAsync(factory, otherTenantId)).Permissions,
            p => p.StartsWith("operator.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACustomRoleCannotCarryOperatorPermissionsEvenInTheOperatorTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        using var client = StubClient(factory, Guid.CreateVersion7(), OperatorTenantId, "advisorship.roles.manage");

        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{OperatorTenantId}/authorization/roles",
            new { key = "operador", displayName = "Operador", description = "", permissions = OnlyTenantsRead },
            TestContext.Current.CancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "authorization.role.permission_operator_only");
    }

    private static async Task<CatalogPayload> CatalogAsync(QepApiFactory factory, Guid tenantId)
    {
        using var client = StubClient(factory, Guid.CreateVersion7(), tenantId, "advisorship.read");
        var catalog = await client.GetFromJsonAsync<CatalogPayload>(
            $"/api/v1/tenants/{tenantId}/authorization/catalog", TestContext.Current.CancellationToken);
        Assert.NotNull(catalog);
        return catalog;
    }

    private static async Task<RolePayload> AdminRoleAsync(QepApiFactory factory, Guid tenantId)
    {
        using var client = StubClient(factory, Guid.CreateVersion7(), tenantId, "advisorship.read");
        var roles = await client.GetFromJsonAsync<List<RolePayload>>(
            $"/api/v1/tenants/{tenantId}/authorization/roles", TestContext.Current.CancellationToken);
        Assert.NotNull(roles);
        return roles.Single(role => role.Role == "admin");
    }

    internal static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(TestContext.Current.CancellationToken);
        Assert.Equal(code, problem?.Code);
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

    private sealed record CatalogPayload(string CatalogVersion, List<CatalogRole> Roles, List<CatalogPermission> Permissions);
    private sealed record CatalogRole(string Role, string[] Permissions);
    private sealed record CatalogPermission(string Permission);
    private sealed record RolePayload(string Role, string[] Permissions);
    internal sealed record ProblemPayload(string Code);
}
