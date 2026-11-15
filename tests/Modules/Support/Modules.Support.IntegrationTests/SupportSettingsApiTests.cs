using System.Net;
using System.Text.Json;
using Modules.Support.Application;
using static Modules.Support.IntegrationTests.SupportApiHarness;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// El umbral de «por vencer» del tenant [owner 2026-11-14]: 3 días hábiles por defecto, de 0 a 30, editable con
/// <c>support.settings.manage</c> e <c>If-Match</c>, y legible con leer, gestionar o configurar. Comparten host: cada prueba usa su
/// tenant.
/// </summary>
public sealed class SupportSettingsApiTests(SharedSupportHost sharedHost) : IClassFixture<SharedSupportHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WithoutSettingsTheDueSoonIs3()
    {
        var (_, client, tenant) = await NewTenantAsync(SupportPermissions.Read);

        var response = await client.GetAsync(SettingsUrl(tenant), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((3, 0, 30, 1L), Settings(await JsonAsync(response)));
        Assert.Equal("\"1\"", response.Headers.ETag?.Tag);
    }

    [Fact]
    public async Task TheAdminSetsItTo5()
    {
        var (factory, client, tenant) = await NewTenantAsync(SupportPermissions.Read, SupportPermissions.SettingsManage);

        var response = await PutAsync(client, SettingsUrl(tenant), new { dueSoonBusinessDays = 5 }, version: 1);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((5, 0, 30, 2L), Settings(await JsonAsync(response)));
        Assert.Equal("\"2\"", response.Headers.ETag?.Tag);
        Assert.Equal(1, await CountAuditEntriesAsync(factory, SupportAuditActions.SettingsUpdated, tenant));
        Assert.Equal((5, 0, 30, 2L), Settings(await JsonAsync(await client.GetAsync(SettingsUrl(tenant), Ct))));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(31)]
    public async Task OutOfRangeIs422OnTheField(int value)
    {
        var (factory, client, tenant) = await NewTenantAsync(SupportPermissions.Read, SupportPermissions.SettingsManage);

        var response = await PutAsync(client, SettingsUrl(tenant), new { dueSoonBusinessDays = value }, version: 1);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("validation.failed", await CodeOfAsync(response));
        Assert.Equal(["DueSoonBusinessDays"], await ErrorKeysAsync(response));
        Assert.Equal((3, 0, 30, 1L), Settings(await JsonAsync(await client.GetAsync(SettingsUrl(tenant), Ct))));
        Assert.Equal(0, await CountAuditEntriesAsync(factory, SupportAuditActions.SettingsUpdated, tenant));
    }

    [Fact]
    public async Task ZeroIsAccepted()
    {
        var (_, client, tenant) = await NewTenantAsync(SupportPermissions.SettingsManage);

        var response = await PutAsync(client, SettingsUrl(tenant), new { dueSoonBusinessDays = 0 }, version: 1);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((0, 0, 30, 2L), Settings(await JsonAsync(response)));
    }

    [Fact]
    public async Task WithoutIfMatchIs428AndWithAnOldVersionIs412()
    {
        var (_, client, tenant) = await NewTenantAsync(SupportPermissions.SettingsManage);

        var withoutVersion = await PutAsync(client, SettingsUrl(tenant), new { dueSoonBusinessDays = 5 }, version: null);
        var saved = await PutAsync(client, SettingsUrl(tenant), new { dueSoonBusinessDays = 5 }, version: 1);
        var stale = await PutAsync(client, SettingsUrl(tenant), new { dueSoonBusinessDays = 7 }, version: 1);

        Assert.Equal(HttpStatusCode.PreconditionRequired, withoutVersion.StatusCode);
        Assert.Equal("precondition.if_match_required", await CodeOfAsync(withoutVersion));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal("concurrency.conflict", await CodeOfAsync(stale));
        Assert.Equal((5, 0, 30, 2L), Settings(await JsonAsync(await client.GetAsync(SettingsUrl(tenant), Ct))));
    }

    [Fact]
    public async Task AnAdvisorCannotChangeIt()
    {
        var (_, client, tenant) = await NewTenantAsync(SupportPermissions.Read, SupportPermissions.Manage);

        var read = await client.GetAsync(SettingsUrl(tenant), Ct);
        var write = await PutAsync(client, SettingsUrl(tenant), new { dueSoonBusinessDays = 5 }, version: 1);

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal((3, 0, 30, 1L), Settings(await JsonAsync(read)));
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        Assert.Equal((3, 0, 30, 1L), Settings(await JsonAsync(await client.GetAsync(SettingsUrl(tenant), Ct))));
    }

    [Fact]
    public async Task ARoleWithOnlySettingsManageReadsAndSavesTheThreshold()
    {
        var (_, client, tenant) = await NewTenantAsync(SupportPermissions.SettingsManage);

        var read = await client.GetAsync(SettingsUrl(tenant), Ct);
        var write = await PutAsync(client, SettingsUrl(tenant), new { dueSoonBusinessDays = 7 }, version: 1);

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal((3, 0, 30, 1L), Settings(await JsonAsync(read)));
        Assert.Equal(HttpStatusCode.OK, write.StatusCode);
        Assert.Equal((7, 0, 30, 2L), Settings(await JsonAsync(write)));
    }

    private static string SettingsUrl(string tenant) => $"{SupportUrl(tenant)}/settings";

    private static (int DueSoon, int Min, int Max, long Version) Settings(JsonElement body) => (
        body.GetProperty("dueSoonBusinessDays").GetInt32(),
        body.GetProperty("minDueSoonBusinessDays").GetInt32(),
        body.GetProperty("maxDueSoonBusinessDays").GetInt32(),
        body.GetProperty("version").GetInt64());

    private async Task<(QepApiFactory Factory, HttpClient Client, string Tenant)> NewTenantAsync(params string[] permissions)
    {
        var factory = await sharedHost.StartTestAsync();
        var tenant = NewTenantId();
        await SeedTenantAsync(factory, tenant);
        return (factory, CreateClient(factory, SubjectId, tenant, permissions), tenant);
    }
}
