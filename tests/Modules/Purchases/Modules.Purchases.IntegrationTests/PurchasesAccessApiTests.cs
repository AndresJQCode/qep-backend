using System.Net;
using System.Net.Http.Json;
using Modules.Purchases.Application;
using Modules.Tenancy.Domain;
using static Modules.Purchases.IntegrationTests.PurchasesApiHarness;

namespace Modules.Purchases.IntegrationTests;

/// <summary>Quién entra a Compras: permisos, tenant de la ruta, módulo habilitado y membresía activa.</summary>
public sealed class PurchasesAccessApiTests(SharedPurchasesHost host) : IClassFixture<SharedPurchasesHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private QepApiFactory Factory => host.Factory;

    [Fact]
    public async Task AReaderCanReadButNotWrite()
    {
        using var scenario = await NewScenarioAsync(Factory);
        var body = OrderBody(scenario.SupplierId, (scenario.VariantA, 2m, 100m));
        var order = await CreateOrderAsync(scenario.Manager, scenario.TenantId, body);
        using var reader = CreateReader(Factory, scenario.Tenant);
        var url = OrderUrl(scenario.TenantId, order.Id);

        var list = await reader.GetAsync(OrderUrl(scenario.TenantId), Ct);
        var detail = await reader.GetAsync(url, Ct);
        var create = await reader.PostAsJsonAsync(OrderUrl(scenario.TenantId), body, Ct);
        var update = await WriteAsync(reader, HttpMethod.Put, url, order.Version, body);
        var send = await WriteAsync(reader, HttpMethod.Post, $"{url}/send", order.Version);

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Empty((await detail.Content.ReadFromJsonAsync<PurchaseOrderDetailDto>(Ct))!.AllowedActions);
        Assert.All(
            new[] { create.StatusCode, update.StatusCode, send.StatusCode },
            status => Assert.Equal(HttpStatusCode.Forbidden, status));
    }

    [Fact]
    public async Task AnotherTenantsRouteIs403AuthorizationDenied()
    {
        using var scenario = await NewScenarioAsync(Factory);
        var order = await CreateOrderAsync(
            scenario.Manager, scenario.TenantId, OrderBody(scenario.SupplierId, (scenario.VariantA, 2m, 100m)));
        var outsiderTenant = await NewTenantAsync(Factory);
        using var outsider = CreateManager(Factory, outsiderTenant);

        var list = await outsider.GetAsync(OrderUrl(scenario.TenantId), Ct);
        var detail = await outsider.GetAsync(OrderUrl(scenario.TenantId, order.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal("authorization.denied", await CodeOfAsync(list));
        Assert.Equal(HttpStatusCode.Forbidden, detail.StatusCode);
        Assert.Equal("authorization.denied", await CodeOfAsync(detail));
    }

    [Fact]
    public async Task WithThePurchasesModuleOffEveryEndpointIs403()
    {
        var tenant = await NewTenantAsync(Factory, TenantModuleKeys.All.Where(key => key != TenantModuleKeys.Purchases));
        using var manager = CreateManager(Factory, tenant);

        var list = await manager.GetAsync(OrderUrl(tenant.TenantId), Ct);
        var detail = await manager.GetAsync(OrderUrl(tenant.TenantId, Guid.CreateVersion7()), Ct);
        var create = await manager.PostAsJsonAsync(
            OrderUrl(tenant.TenantId), OrderBody(Guid.CreateVersion7(), (Guid.CreateVersion7(), 1m, 10m)), Ct);

        Assert.All(
            new[] { list.StatusCode, detail.StatusCode, create.StatusCode },
            status => Assert.Equal(HttpStatusCode.Forbidden, status));
    }

    [Fact]
    public async Task WithoutAnActiveMembershipCreateIs403()
    {
        using var scenario = await NewScenarioAsync(Factory);
        using var stranger = CreateClient(
            Factory,
            Guid.CreateVersion7(),
            scenario.TenantId,
            PurchasesPermissions.OrderRead,
            PurchasesPermissions.OrderManage);

        var response = await stranger.PostAsJsonAsync(
            OrderUrl(scenario.TenantId), OrderBody(scenario.SupplierId, (scenario.VariantA, 2m, 100m)), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("authorization.denied", await CodeOfAsync(response));
    }
}
