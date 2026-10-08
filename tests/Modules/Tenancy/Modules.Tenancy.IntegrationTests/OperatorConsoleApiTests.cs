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
}
