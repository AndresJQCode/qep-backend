using System.Net;
using static Modules.Store.IntegrationTests.StoreApiHarness;

namespace Modules.Store.IntegrationTests;

/// <summary>Quién puede tocar los ajustes: el permiso, el tenant de la ruta y el módulo. Siempre 403, nunca 404.</summary>
public sealed class StoreAccessApiTests
{
    [Fact]
    public async Task WithoutThePermissionSettingsAre403()
    {
        await using var fixture = await StoreSettingsFixture.OpenAsync();
        var without = fixture.ClientFor(TenantId, "companies.company.read");

        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.GetAsync(client: without)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.PutAsync(fixture.Body(), client: without)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.PostAsync("/publish", ifMatch: "1", client: without)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.PostAsync("/unpublish", ifMatch: "1", client: without)).StatusCode);
    }

    [Fact]
    public async Task AnotherTenantInTheRouteIs403Never404()
    {
        await using var fixture = await StoreSettingsFixture.OpenAsync();
        await SeedTenantAsync(fixture.Factory, OtherTenantId);

        // El llamador es del tenant A y tiene el permiso, pero la ruta nombra al tenant B: el handler lo frena.
        var read = await fixture.GetAsync(tenantId: OtherTenantId);
        var write = await fixture.PutAsync(fixture.Body(), tenantId: OtherTenantId);

        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        Assert.Equal("authorization.denied", await StoreSettingsFixture.CodeAsync(read));
    }

    [Fact]
    public async Task WithTheModuleOffSettingsAre403()
    {
        await using var fixture = await StoreSettingsFixture.OpenAsync(
            Modules.Tenancy.Domain.TenantModuleKeys.All.Where(key => key != Modules.Tenancy.Domain.TenantModuleKeys.Store));

        var read = await fixture.GetAsync();
        var write = await fixture.PutAsync(fixture.Body());

        // store.settings.manage exige el módulo (RequiredModules), así que ModuleEntitlementMask lo quita antes del handler: 403 pelado,
        // sin código de módulo, igual que en Loans (LoansAccessApiTests.WithTheModuleOffTheRouteIs403). La bandeja, que cuelga de un
        // permiso de Quotations, sí responde tenancy.module_not_enabled desde su handler.
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.DoesNotContain("tenancy.module_not_enabled", await BodyAsync(read));
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/order-requests")]
    [InlineData("GET", "/order-requests/summary")]
    [InlineData("GET", "/order-requests/0195f0a0-0000-7000-8000-000000000001")]
    [InlineData("GET", "/order-requests/0195f0a0-0000-7000-8000-000000000001/customer-candidates")]
    [InlineData("POST", "/order-requests/0195f0a0-0000-7000-8000-000000000001/take")]
    [InlineData("POST", "/order-requests/0195f0a0-0000-7000-8000-000000000001/discard")]
    public async Task TheInboxRoutesAreGone(string method, string path)
    {
        // La bandeja se quitó (spec 2026-11-09, D6): con el permiso de pedidos y el módulo encendido, la ruta ya no existe.
        await using var fixture = await StoreSettingsFixture.OpenAsync();
        var client = fixture.ClientFor(TenantId, "quotations.order.manage", "store.settings.manage");
        using var request = new HttpRequestMessage(
            new HttpMethod(method), $"/api/v1/tenants/{TenantId}/store{path}")
        {
            Content = method == "POST" ? new StringContent("{}", System.Text.Encoding.UTF8, "application/json") : null,
        };

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
