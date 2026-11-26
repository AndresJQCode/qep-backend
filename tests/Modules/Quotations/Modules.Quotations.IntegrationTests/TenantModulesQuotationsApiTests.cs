using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Customers.Application;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;
using ModuleKeys = Modules.Tenancy.Domain.TenantModuleKeys;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// Módulos por tenant sobre cotizaciones y pedidos (spec 2026-10-07): un tenant registrado nace con
/// los seis, y apagar uno corta el acceso en el request siguiente.
/// </summary>
public sealed class TenantModulesQuotationsApiTests(SharedQuotationsHost sharedHost) : IClassFixture<SharedQuotationsHost>
{
    [Fact]
    public async Task QuotationsAreForbiddenOnceTheModuleIsTurnedOff()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.GetAsync(QuotationsUrl(tenantId), TestContext.Current.CancellationToken)).StatusCode);

        await DisableModuleAsync(factory, tenantId, ModuleKeys.Quotations);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.GetAsync(QuotationsUrl(tenantId), TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task EffectivePermissionsDropQuotationsOnceTurnedOff()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;

        await DisableModuleAsync(factory, tenantId, ModuleKeys.Quotations);

        var permissions = await EffectivePermissionsAsync(client, tenantId);
        Assert.DoesNotContain(permissions, permission => permission.StartsWith("quotations.quotation.", StringComparison.Ordinal));
        Assert.Contains(CustomersPermissions.CustomerRead, permissions);
    }

    // Review Focus 1: volver a prender devuelve todo, en el request siguiente y sin reiniciar.
    [Fact]
    public async Task TurningCustomersBackOnRestoresQuotations()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;

        await DisableModuleAsync(factory, tenantId, ModuleKeys.Customers);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.GetAsync(QuotationsUrl(tenantId), TestContext.Current.CancellationToken)).StatusCode);

        await EnableModuleAsync(factory, tenantId, ModuleKeys.Customers);
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.GetAsync(QuotationsUrl(tenantId), TestContext.Current.CancellationToken)).StatusCode);
    }

    // Regresión del spec: el dato de otro módulo dentro de una respuesta de un módulo prendido.
    // ListQuotationsHandler ya pide los pedidos sólo con quotations.order.read
    // (ListQuotations.cs:170-177), y ese permiso queda enmascarado sin orders.
    [Fact]
    public async Task TheListHidesTheOrderOnceOrdersIsTurnedOff()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, customerId, productId);
        var converted = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/order",
            new ConvertQuotationToOrderRequest(null, []),
            TestContext.Current.CancellationToken);
        converted.EnsureSuccessStatusCode();
        Assert.NotNull(Assert.Single((await ListAsync(client, tenantId)).Items).OrderId);

        await DisableModuleAsync(factory, tenantId, ModuleKeys.Orders);

        var row = Assert.Single((await ListAsync(client, tenantId)).Items);
        Assert.Null(row.OrderId);
        Assert.Null(row.OrderStatus);
    }

    // Spec 2026-10-07, «Catálogo de roles filtrado»: ni en permissions ni en los roles. El editor de
    // roles sólo pinta checkboxes del catálogo, así que un permiso de un módulo apagado no se ofrece.
    [Fact]
    public async Task TheRoleCatalogHidesThePermissionsOfAModuleThatIsOff()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(
            factory, [.. ManagerPermissions, Modules.Tenancy.Application.TenancyPermissions.AdvisorshipRead]);
        using var _ = client;
        Assert.Contains(
            (await CatalogAsync(client, tenantId)).Permissions,
            permission => permission.Permission == QuotationsPermissions.QuotationRead);

        await DisableModuleAsync(factory, tenantId, ModuleKeys.Quotations);

        var catalog = await CatalogAsync(client, tenantId);
        // Los permisos de pedidos (quotations.order.*) son del módulo orders, que ya no depende de quotations: sólo
        // desaparecen los de cotizaciones.
        Assert.DoesNotContain(
            catalog.Permissions,
            permission => permission.Permission.StartsWith("quotations.quotation.", StringComparison.Ordinal));
        Assert.Contains(
            catalog.Permissions, permission => permission.Permission == OrdersPermissions.OrderRead);
        Assert.NotEmpty(catalog.Roles);
        Assert.All(catalog.Roles, role => Assert.DoesNotContain(
            role.Permissions, permission => permission.StartsWith("quotations.quotation.", StringComparison.Ordinal)));
        Assert.Contains(
            catalog.Permissions, permission => permission.Permission == CustomersPermissions.CustomerRead);
    }

    [Fact]
    public async Task TheOrdersExportLayoutNeedsOrders()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(
            factory, [.. ManagerPermissions, Modules.Tenancy.Application.TenancyPermissions.SettingsRead]);
        using var _ = client;
        var url = $"/api/v1/tenants/{tenantId}/orders-export-layout";
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.GetAsync(url, TestContext.Current.CancellationToken)).StatusCode);

        await DisableModuleAsync(factory, tenantId, ModuleKeys.Orders);

        var response = await client.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var problem = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("tenancy.module_not_enabled", problem.RootElement.GetProperty("code").GetString());
    }

    // Un pedido nace sin cotización (spec 2026-10-15): el dueño de la plataforma oculta cotizaciones y los pedidos,
    // sus permisos y las opciones de envío del formulario de pedido siguen funcionando.
    [Fact]
    public async Task OrdersAndShippingOptionsSurviveHidingQuotations()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(
            factory, [.. ManagerPermissions, Modules.Tenancy.Application.TenancyPermissions.SettingsRead]);
        using var _ = client;
        var ordersUrl = $"/api/v1/tenants/{tenantId}/orders";
        var shippingUrl = $"/api/v1/tenants/{tenantId}/shipping-options";
        var carriersUrl = $"/api/v1/tenants/{tenantId}/carriers";

        await DisableModuleAsync(factory, tenantId, ModuleKeys.Quotations);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.GetAsync(QuotationsUrl(tenantId), TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.GetAsync(ordersUrl, TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.GetAsync(shippingUrl, TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.GetAsync(carriersUrl, TestContext.Current.CancellationToken)).StatusCode);
        var permissions = await EffectivePermissionsAsync(client, tenantId);
        Assert.Contains(OrdersPermissions.OrderRead, permissions);
        Assert.DoesNotContain(QuotationsPermissions.QuotationRead, permissions);
    }

    // La otra cara: quien sólo cotiza (sin pedidos) conserva transportadoras y opciones de envío.
    [Fact]
    public async Task ShippingOptionsSurviveHidingOrders()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(
            factory, [.. ManagerPermissions, Modules.Tenancy.Application.TenancyPermissions.SettingsRead]);
        using var _ = client;

        await DisableModuleAsync(factory, tenantId, ModuleKeys.Orders);

        Assert.Equal(
            HttpStatusCode.OK,
            (await client.GetAsync($"/api/v1/tenants/{tenantId}/shipping-options", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.GetAsync($"/api/v1/tenants/{tenantId}/carriers", TestContext.Current.CancellationToken)).StatusCode);
    }

    // Sin ninguno de los dos módulos no hay nada que enviar: la configuración compartida se cierra.
    [Fact]
    public async Task ShippingOptionsAreForbiddenWithoutQuotationsNorOrders()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(
            factory, [.. ManagerPermissions, Modules.Tenancy.Application.TenancyPermissions.SettingsRead]);
        using var _ = client;

        await DisableModuleAsync(factory, tenantId, ModuleKeys.Quotations);
        await DisableModuleAsync(factory, tenantId, ModuleKeys.Orders);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.GetAsync($"/api/v1/tenants/{tenantId}/shipping-options", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.GetAsync($"/api/v1/tenants/{tenantId}/carriers", TestContext.Current.CancellationToken)).StatusCode);
    }

    private static async Task<CatalogDto> CatalogAsync(HttpClient client, Guid tenantId)
    {
        var catalog = await client.GetFromJsonAsync<CatalogDto>(
            $"/api/v1/tenants/{tenantId}/authorization/catalog", TestContext.Current.CancellationToken);
        Assert.NotNull(catalog);
        return catalog;
    }

    private sealed record CatalogDto(string CatalogVersion, CatalogRoleDto[] Roles, CatalogPermissionDto[] Permissions);

    private sealed record CatalogRoleDto(string Role, string[] Permissions);

    private sealed record CatalogPermissionDto(string Permission);

    private static async Task<QuotationsPageResponse> ListAsync(HttpClient client, Guid tenantId)
    {
        var page = await client.GetFromJsonAsync<QuotationsPageResponse>(
            QuotationsUrl(tenantId), TestContext.Current.CancellationToken);
        Assert.NotNull(page);
        return page;
    }

    private static async Task<string[]> EffectivePermissionsAsync(HttpClient client, Guid tenantId)
    {
        var response = await client.GetFromJsonAsync<EffectivePermissionsDto>(
            $"/api/v1/tenants/{tenantId}/authorization/me", TestContext.Current.CancellationToken);
        Assert.NotNull(response);
        return response.Permissions;
    }

    private sealed record EffectivePermissionsDto(Guid TenantId, string[] Permissions);
}
