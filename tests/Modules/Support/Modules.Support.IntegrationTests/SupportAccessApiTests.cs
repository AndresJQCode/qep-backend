using System.Net;
using Modules.Support.Application;
using Modules.Tenancy.Domain;
using static Modules.Support.IntegrationTests.SupportApiHarness;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// Las dos capas del aislamiento de tenant: la política frena a quien no tiene el permiso y el handler a quien lo tiene en otro
/// tenant, con 403 y nunca 404. Con el módulo apagado también es 403. Comparten host: cada prueba siembra sus tenants, y el del
/// módulo apagado se siembra sin <c>support</c>, sin tocar nada global.
/// </summary>
public sealed class SupportAccessApiTests(SharedSupportHost sharedHost) : IClassFixture<SharedSupportHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WithoutThePermissionTheTypesAre403()
    {
        var (factory, tenant) = await NewTenantAsync();
        using var client = CreateClient(factory, SubjectId, tenant, "tenancy.settings.read");

        var response = await client.GetAsync($"{SupportUrl(tenant)}/ticket-types", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AnotherTenantInTheRouteIs403Never404()
    {
        var (factory, mine) = await NewTenantAsync();
        var (_, other) = await NewTenantAsync();
        using var client = CreateClient(factory, SubjectId, mine, SupportPermissions.Read, SupportPermissions.Manage);

        var response = await client.GetAsync($"{SupportUrl(other)}/ticket-types", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("authorization.denied", await CodeOfAsync(response));
    }

    [Fact]
    public async Task WithTheModuleOffTheRouteIs403()
    {
        var (factory, tenant) = await NewTenantAsync(TenantModuleKeys.All.Where(key => key != TenantModuleKeys.Support));
        using var client = CreateClient(factory, SubjectId, tenant, SupportPermissions.Read, SupportPermissions.Manage);

        var response = await client.GetAsync($"{SupportUrl(tenant)}/ticket-types", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AMemberWithOnlyManageReadsTheTypes()
    {
        var (factory, tenant) = await NewTenantAsync();
        using var client = CreateClient(factory, SubjectId, tenant, SupportPermissions.Manage);

        var response = await client.GetAsync($"{SupportUrl(tenant)}/ticket-types", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(8, (await JsonAsync(response)).GetArrayLength());
    }

    [Fact]
    public async Task SettingsNeedSettingsManage()
    {
        var (factory, tenant) = await NewTenantAsync();
        using var client = CreateClient(factory, SubjectId, tenant, SupportPermissions.Read, SupportPermissions.Manage);

        var response = await client.GetAsync($"{SupportUrl(tenant)}/settings/ticket-types", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task<(QepApiFactory Factory, string Tenant)> NewTenantAsync(IEnumerable<TenantModuleKey>? modules = null)
    {
        var factory = await sharedHost.StartTestAsync();
        var tenant = NewTenantId();
        await SeedTenantAsync(factory, tenant, modules);
        return (factory, tenant);
    }
}
