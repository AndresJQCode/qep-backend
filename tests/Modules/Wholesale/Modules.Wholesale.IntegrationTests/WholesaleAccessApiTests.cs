using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Authorization.Application;
using Modules.Tenancy.Domain;
using Modules.Wholesale.Application;

namespace Modules.Wholesale.IntegrationTests;

/// <summary>
/// Quién entra a las rutas del mayorista (Global Constraints, aislamiento en doble capa): la política por permiso y módulo, y
/// <c>WholesaleAuthorization</c> en el handler para el tenant de la ruta.
/// </summary>
public sealed class WholesaleAccessApiTests(SharedWholesaleHost host) : IClassFixture<SharedWholesaleHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WholesaleApiHarness.QepApiFactory Factory => host.Factory;

    [Fact]
    public async Task AReaderSeesDistributorsWithEmptyAllowedActionsAndCannotWrite()
    {
        var tenant = await WholesaleApiHarness.NewTenantAsync(Factory);
        using var manager = WholesaleApiHarness.CreateManager(Factory, tenant);
        var list = await WholesaleApiHarness.CreatePriceListAsync(manager, tenant.TenantId, "Oro", 10m);
        var company = await WholesaleApiHarness.CreateCompanyAsync(manager, tenant.TenantId, "Mayorista Central SAS");
        var (distributor, _) = await WholesaleApiHarness.InviteAsync(Factory, manager, tenant, list.Id, company);
        using var reader = WholesaleApiHarness.CreateReader(Factory, tenant);
        var url = $"{WholesaleApiHarness.DistributorsUrl(tenant.TenantId)}/{distributor.Id}";

        var listed = await reader.GetFromJsonAsync<DistributorsResponse>(WholesaleApiHarness.DistributorsUrl(tenant.TenantId), Ct);
        var detail = await reader.GetFromJsonAsync<DistributorDetailDto>(url, Ct);
        var resend = await reader.SendAsync(
            WholesaleApiHarness.WithIfMatch(new HttpRequestMessage(HttpMethod.Post, $"{url}/resend-invitation"), distributor.Version), Ct);
        // «Copiar» rota el token: es una escritura, como reenviar.
        var rotate = await reader.SendAsync(
            WholesaleApiHarness.WithIfMatch(new HttpRequestMessage(HttpMethod.Post, $"{url}/invitation-link"), distributor.Version), Ct);
        var options = await reader.GetAsync($"{WholesaleApiHarness.DistributorsUrl(tenant.TenantId)}/options", Ct);
        var lists = await reader.GetAsync(WholesaleApiHarness.PriceListsUrl(tenant.TenantId), Ct);

        var row = Assert.Single(listed!.Items);
        Assert.False(row.Invitation!.CanResend);
        Assert.Empty(detail!.AllowedActions);
        Assert.Equal(HttpStatusCode.Forbidden, resend.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, rotate.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, options.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, lists.StatusCode);
    }

    // P-3: el rol advisor trae la lectura de distribuidores y nada más del módulo.
    [Fact]
    public async Task AnAdvisorReadsDistributors()
    {
        var tenant = await WholesaleApiHarness.NewTenantAsync(Factory);
        var advisor = Factory.Services.GetServices<RoleTemplate>().Single(role => role.Key == RoleTemplateKeys.Advisor);
        using var client = WholesaleApiHarness.CreateClient(Factory, tenant.SubjectId, tenant.TenantId, [.. advisor.Permissions]);

        var distributors = await client.GetAsync(WholesaleApiHarness.DistributorsUrl(tenant.TenantId), Ct);
        var invite = await client.PostAsJsonAsync(WholesaleApiHarness.DistributorsUrl(tenant.TenantId), new { }, Ct);
        var lists = await client.GetAsync(WholesaleApiHarness.PriceListsUrl(tenant.TenantId), Ct);

        Assert.Contains(WholesalePermissions.DistributorRead, advisor.Permissions);
        Assert.Equal(HttpStatusCode.OK, distributors.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, invite.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, lists.StatusCode);
    }

    [Fact]
    public async Task WithoutTheWholesaleModuleEverythingIs403()
    {
        var tenant = await WholesaleApiHarness.NewTenantAsync(
            Factory, TenantModuleKeys.All.Where(key => key != TenantModuleKeys.Wholesale));
        using var manager = WholesaleApiHarness.CreateManager(Factory, tenant);

        // Spec 2026-11-22: las listas de precios son de Catalog y no exigen el módulo; las rutas del distribuidor sí.
        foreach (var url in new[]
        {
            WholesaleApiHarness.DistributorsUrl(tenant.TenantId),
            $"{WholesaleApiHarness.DistributorsUrl(tenant.TenantId)}/options",
            WholesaleApiHarness.CategoriesUrl(tenant.TenantId),
        })
        {
            var response = await manager.GetAsync(url, Ct);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{url} answered {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task AnotherTenantIs403()
    {
        var mine = await WholesaleApiHarness.NewTenantAsync(Factory);
        var other = await WholesaleApiHarness.NewTenantAsync(Factory);
        using var otherManager = WholesaleApiHarness.CreateManager(Factory, other);
        var list = await WholesaleApiHarness.CreatePriceListAsync(otherManager, other.TenantId, "Oro", 10m);
        // Mis permisos, en mi tenant, contra la ruta del otro.
        using var manager = WholesaleApiHarness.CreateManager(Factory, mine);

        var lists = await manager.GetAsync(WholesaleApiHarness.PriceListsUrl(other.TenantId), Ct);
        var detail = await manager.GetAsync($"{WholesaleApiHarness.PriceListsUrl(other.TenantId)}/{list.Id}", Ct);
        var distributors = await manager.GetAsync(WholesaleApiHarness.DistributorsUrl(other.TenantId), Ct);
        var categories = await manager.GetAsync(WholesaleApiHarness.CategoriesUrl(other.TenantId), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, lists.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, detail.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, distributors.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, categories.StatusCode);
        Assert.Equal("authorization.denied", await WholesaleApiHarness.CodeOfAsync(lists));
    }
}
