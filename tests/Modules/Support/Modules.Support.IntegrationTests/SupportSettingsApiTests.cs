using System.Net;
using System.Text.Json;
using Modules.Support.Application;
using static Modules.Support.IntegrationTests.SupportApiHarness;
using static Modules.Support.IntegrationTests.SupportMessagingSeed;

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

        var response = await PutAsync(client, SettingsUrl(tenant), new { dueSoonBusinessDays = 5, surveyEnabled = false, autoCloseBusinessDays = 5 }, version: 1);

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

        var response = await PutAsync(client, SettingsUrl(tenant), new { dueSoonBusinessDays = value, surveyEnabled = false, autoCloseBusinessDays = 5 }, version: 1);

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

        var response = await PutAsync(client, SettingsUrl(tenant), new { dueSoonBusinessDays = 0, surveyEnabled = false, autoCloseBusinessDays = 5 }, version: 1);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((0, 0, 30, 2L), Settings(await JsonAsync(response)));
    }

    [Fact]
    public async Task WithoutIfMatchIs428AndWithAnOldVersionIs412()
    {
        var (_, client, tenant) = await NewTenantAsync(SupportPermissions.SettingsManage);

        var withoutVersion = await PutAsync(client, SettingsUrl(tenant), new { dueSoonBusinessDays = 5, surveyEnabled = false, autoCloseBusinessDays = 5 }, version: null);
        var saved = await PutAsync(client, SettingsUrl(tenant), new { dueSoonBusinessDays = 5, surveyEnabled = false, autoCloseBusinessDays = 5 }, version: 1);
        var stale = await PutAsync(client, SettingsUrl(tenant), new { dueSoonBusinessDays = 7, surveyEnabled = false, autoCloseBusinessDays = 5 }, version: 1);

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
        var write = await PutAsync(client, SettingsUrl(tenant), new { dueSoonBusinessDays = 5, surveyEnabled = false, autoCloseBusinessDays = 5 }, version: 1);

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
        var write = await PutAsync(client, SettingsUrl(tenant), new { dueSoonBusinessDays = 7, surveyEnabled = false, autoCloseBusinessDays = 5 }, version: 1);

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal((3, 0, 30, 1L), Settings(await JsonAsync(read)));
        Assert.Equal(HttpStatusCode.OK, write.StatusCode);
        Assert.Equal((7, 0, 30, 2L), Settings(await JsonAsync(write)));
    }

    // Fase 4b (H6): el «siguiente paso» del tenant. El PUT reemplaza todo: mandarlo en null lo borra. La auditoría nombra el campo,
    // nunca el texto.
    [Fact]
    public async Task TheTenantNextStepRoundTripsAndIsAudited()
    {
        var (factory, client, tenant) = await NewTenantAsync(SupportPermissions.Read, SupportPermissions.SettingsManage);
        const string text = "Si no estás de acuerdo, puedes acudir a la Superintendencia.\nLínea dos";

        var before = await JsonAsync(await client.GetAsync(SettingsUrl(tenant), Ct));
        var saved = await PutAsync(
            client, SettingsUrl(tenant), new { dueSoonBusinessDays = 3, formalNextStep = $"  {text}  ", surveyEnabled = false, autoCloseBusinessDays = 5 }, version: 1);

        Assert.Equal(JsonValueKind.Null, before.GetProperty("formalNextStep").ValueKind);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(text, (await JsonAsync(saved)).GetProperty("formalNextStep").GetString());
        var read = await JsonAsync(await client.GetAsync(SettingsUrl(tenant), Ct));
        Assert.Equal(text, read.GetProperty("formalNextStep").GetString());
        Assert.Equal(2L, read.GetProperty("version").GetInt64());
        Assert.Equal(
            "[\"formalNextStep\"]",
            await TextAsync(
                factory,
                "SELECT changed_fields::text FROM audit.entries WHERE action = @action AND resource_id = @tenant",
                ("action", SupportAuditActions.SettingsUpdated),
                ("tenant", tenant)));
        Assert.Equal(
            0L,
            await ScalarAsync(
                factory,
                "SELECT count(*) FROM audit.entries WHERE resource_id = @tenant AND changed_fields::text LIKE '%Superintendencia%'",
                ("tenant", tenant)));

        var cleared = await PutAsync(
            client, SettingsUrl(tenant), new { dueSoonBusinessDays = 3, formalNextStep = (string?)null, surveyEnabled = false, autoCloseBusinessDays = 5 }, version: 2);

        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await JsonAsync(cleared)).GetProperty("formalNextStep").ValueKind);
    }

    [Fact]
    public async Task ANextStepOver2000IsRejectedOnTheField()
    {
        var (_, client, tenant) = await NewTenantAsync(SupportPermissions.SettingsManage);

        var response = await PutAsync(
            client, SettingsUrl(tenant), new { dueSoonBusinessDays = 3, formalNextStep = new string('a', 2001), surveyEnabled = false, autoCloseBusinessDays = 5 }, version: 1);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(["FormalNextStep"], await ErrorKeysAsync(response));
    }

    // Fase 4b (H14, owner 2026-11-16, Q-19 y Q-20): la encuesta nace apagada y el cierre automático en 5 días hábiles, con su rango
    // en la respuesta. La auditoría nombra los dos campos.
    [Fact]
    public async Task TheSurveyToggleAndTheAutoCloseDaysRoundTrip()
    {
        var (factory, client, tenant) = await NewTenantAsync(SupportPermissions.Read, SupportPermissions.SettingsManage);

        var before = await JsonAsync(await client.GetAsync(SettingsUrl(tenant), Ct));
        var saved = await PutAsync(
            client, SettingsUrl(tenant), new { dueSoonBusinessDays = 3, surveyEnabled = true, autoCloseBusinessDays = 8 }, version: 1);

        Assert.Equal((false, 5, 1, 30), Survey(before));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal((true, 8, 1, 30), Survey(await JsonAsync(saved)));
        Assert.Equal((true, 8, 1, 30), Survey(await JsonAsync(await client.GetAsync(SettingsUrl(tenant), Ct))));
        Assert.Equal(
            "[\"surveyEnabled\", \"autoCloseBusinessDays\"]",
            await TextAsync(
                factory,
                "SELECT changed_fields::text FROM audit.entries WHERE action = @action AND resource_id = @tenant",
                ("action", SupportAuditActions.SettingsUpdated),
                ("tenant", tenant)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public async Task AutoCloseOutOfRangeIs422(int value)
    {
        var (_, client, tenant) = await NewTenantAsync(SupportPermissions.SettingsManage);

        var outOfRange = await PutAsync(
            client, SettingsUrl(tenant), new { dueSoonBusinessDays = 3, surveyEnabled = true, autoCloseBusinessDays = value }, version: 1);
        var missing = await PutAsync(client, SettingsUrl(tenant), new { dueSoonBusinessDays = 3 }, version: 1);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, outOfRange.StatusCode);
        Assert.Equal(["AutoCloseBusinessDays"], await ErrorKeysAsync(outOfRange));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
        Assert.Equal(["AutoCloseBusinessDays", "SurveyEnabled"], (await ErrorKeysAsync(missing)).Order(StringComparer.Ordinal));
    }

    private static (bool Enabled, int AutoClose, int Min, int Max) Survey(JsonElement body) => (
        body.GetProperty("surveyEnabled").GetBoolean(),
        body.GetProperty("autoCloseBusinessDays").GetInt32(),
        body.GetProperty("minAutoCloseBusinessDays").GetInt32(),
        body.GetProperty("maxAutoCloseBusinessDays").GetInt32());

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
