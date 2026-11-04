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

        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", await StoreSettingsFixture.CodeAsync(read));
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }
}
