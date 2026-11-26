using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Wms.Domain.Warehousing;
using Modules.Wms.Infrastructure.Persistence;
using Modules.Tenancy.Domain;
using static Modules.Wms.IntegrationTests.WmsApiHarness;

namespace Modules.Wms.IntegrationTests;

/// <summary>
/// El acceso al módulo de Bodegas (spec WMS 2026-10-31 §24.1), de punta a punta por HTTP, sobre <c>GET /warehouses</c>: la política,
/// el tenant de la ruta y el módulo habilitado. Un 403 y nunca un 404 en los tres casos: un 404 confirmaría que el recurso existe.
/// </summary>
public sealed class WmsAccessApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string WarehousesUrl(string tenantId = WmsApiHarness.TenantId) => $"{WmsUrl(tenantId)}/warehouses";

    [Fact]
    public async Task WithoutThePermissionTheWarehousesAre403()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        // Con permisos de otro módulo, pero sin wms.document.read: la política frena antes del handler.
        using var client = CreateClient(factory, SubjectId, WmsApiHarness.TenantId, "tenancy.settings.read");

        var response = await client.GetAsync(WarehousesUrl(), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AnotherTenantInTheRouteIs403Never404()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        await SeedTenantAsync(factory, OtherTenantId);
        // Quien opera en el otro tenant y apunta a la ruta de éste: 403, nunca 404.
        using var outsider = CreateClient(factory, OtherSubjectId, OtherTenantId, WmsApiHarness.AllPermissions);

        var response = await outsider.GetAsync(WarehousesUrl(), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("authorization.denied", await CodeOfAsync(response));
    }

    [Fact]
    public async Task WithTheModuleOffTheWarehousesAre403()
    {
        // Con el módulo apagado la máscara de entitlements quita el permiso antes de que llegue el handler.
        var (database, factory) = await OpenAsync(TenantModuleKeys.All.Where(key => key != TenantModuleKeys.Wms));
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);

        var response = await client.GetAsync(WarehousesUrl(), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(string.Empty, await BodyAsync(response));
    }

    [Fact]
    public async Task WithTheModuleOnAnEmptyTenantListsNoWarehouses()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateReader(factory);

        var response = await client.GetAsync(WarehousesUrl(), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await BodyAsync(response));
        Assert.Equal(0, document.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task TheFirstListProvisionsTheSelfOwnerAndTheSixUnits()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateReader(factory);

        var response = await client.GetAsync(WarehousesUrl(), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WmsDbContext>();
        var tenant = Guid.Parse(WmsApiHarness.TenantId);
        Assert.Equal(1, await db.Owners.CountAsync(o => o.TenantId == tenant && o.Kind == OwnerKind.Self, Ct));
        Assert.Equal(6, await db.UnitsOfMeasure.CountAsync(u => u.TenantId == tenant && u.IsSystem, Ct));
    }
}
