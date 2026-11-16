using System.Net;
using System.Text.Json;
using Modules.Support.Application;
using Modules.Support.Domain;
using static Modules.Support.IntegrationTests.SupportApiHarness;
using static Modules.Support.IntegrationTests.SupportMessagingSeed;
using static Modules.Support.IntegrationTests.SupportPublicHarness;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// The company that answers on the public portal, from Settings › Public form (spec 2026-11-15 §9.3, D-6 and D-8, plan L29 to L32;
/// Review Focus 8). The tenant chooses one of its active companies; it is required to publish, it is still shown if deactivated later,
/// and a company some version chose cannot be deleted (real FK, ON DELETE RESTRICT). Shared host: each test seeds its tenant, its member
/// and its companies, and only reads its own.
/// </summary>
public sealed class SupportFormOrganizationApiTests(SharedSupportHost sharedHost) : IClassFixture<SharedSupportHost>
{
    private const string CompaniesRead = "companies.company.read";

    private const string CompaniesManage = "companies.company.manage";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheSettingsListTheActiveCompaniesAsOptions()
    {
        var rig = await RigAsync(SupportPermissions.SettingsManage);
        var verde = await SeedCompanyAsync(rig.Factory, rig.Tenant, "Verde S.A.S.", "900123456");
        var azul = await SeedCompanyAsync(rig.Factory, rig.Tenant, "Azul Ltda.", "800111222");
        await SeedCompanyAsync(rig.Factory, rig.Tenant, "Apagada S.A.", "700111222", active: false);

        var body = await JsonAsync(await rig.Client.GetAsync(FormUrl(rig.Tenant), Ct));

        var options = body.GetProperty("organizationOptions").EnumerateArray().ToList();
        Assert.Equal([azul, verde], options.Select(option => option.GetProperty("id").GetGuid()));
        Assert.Equal("Azul Ltda.", options[0].GetProperty("name").GetString());
        Assert.Equal("NIT", options[0].GetProperty("documentTypeCode").GetString());
        Assert.Equal("800111222", options[0].GetProperty("documentNumber").GetString());
        Assert.All(options, option => Assert.True(option.GetProperty("isActive").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("organization").ValueKind);
    }

    // The options travel in the settings because GET …/companies needs companies.company.read, which a role with only
    // support.settings.manage may not have.
    [Fact]
    public async Task AManagerWithoutCompaniesPermissionGetsTheOptions()
    {
        var rig = await RigAsync(SupportPermissions.SettingsManage);
        var verde = await SeedCompanyAsync(rig.Factory, rig.Tenant);

        var settings = await rig.Client.GetAsync(FormUrl(rig.Tenant), Ct);
        var companies = await rig.Client.GetAsync($"/api/v1/tenants/{rig.Tenant}/companies", Ct);

        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
        var option = Assert.Single((await JsonAsync(settings)).GetProperty("organizationOptions").EnumerateArray());
        Assert.Equal(verde, option.GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.Forbidden, companies.StatusCode);
    }

    [Fact]
    public async Task ACompanyCanBeChosenAndIsReturned()
    {
        var rig = await RigAsync(SupportPermissions.SettingsManage);
        var verde = await SeedCompanyAsync(rig.Factory, rig.Tenant);

        var response = await PutFormAsync(rig, Form(published: false, verde), version: 0);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal(1, body.GetProperty("version").GetInt32());
        var organization = body.GetProperty("organization");
        Assert.Equal(verde, organization.GetProperty("id").GetGuid());
        Assert.Equal("Verde S.A.S.", organization.GetProperty("name").GetString());
        Assert.Equal("NIT", organization.GetProperty("documentTypeCode").GetString());
        Assert.Equal("900123456", organization.GetProperty("documentNumber").GetString());
        Assert.True(organization.GetProperty("isActive").GetBoolean());
        Assert.Equal(verde.ToString(), await TextAsync(
            rig.Factory,
            "SELECT organization_company_id::text FROM support.form_versions WHERE tenant_id = @tenant AND version = 1",
            ("tenant", rig.TenantId)));
        Assert.Equal(1L, await ScalarAsync(
            rig.Factory,
            """
            SELECT count(*) FROM audit.entries
            WHERE action = @action AND resource_id = @tenant AND changed_fields::text LIKE '%organizationCompanyId%'
            """,
            ("action", SupportAuditActions.FormUpdated),
            ("tenant", rig.Tenant)));
        var read = await JsonAsync(await rig.Client.GetAsync(FormUrl(rig.Tenant), Ct));
        Assert.Equal(verde, read.GetProperty("organization").GetProperty("id").GetGuid());
    }

    // Review Focus 8: a company of another tenant cannot be chosen, nor an id that does not exist. Nothing is saved.
    [Fact]
    public async Task ACompanyOfAnotherTenantIs422()
    {
        var rig = await RigAsync(SupportPermissions.SettingsManage);
        var otherTenant = NewTenantId();
        await SeedTenantAsync(rig.Factory, otherTenant);
        var foreign = await SeedCompanyAsync(rig.Factory, otherTenant, "Ajena S.A.");

        var crossed = await PutFormAsync(rig, Form(published: false, foreign), version: 0);
        var missing = await PutFormAsync(rig, Form(published: false, Guid.CreateVersion7()), version: 0);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, crossed.StatusCode);
        Assert.Equal("support.form.organization_unavailable", await CodeOfAsync(crossed));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
        Assert.Equal("support.form.organization_unavailable", await CodeOfAsync(missing));
        Assert.Equal(0L, await CountFormVersionsAsync(rig));
    }

    [Fact]
    public async Task AnInactiveCompanyCannotBeChosen()
    {
        var rig = await RigAsync(SupportPermissions.SettingsManage);
        var off = await SeedCompanyAsync(rig.Factory, rig.Tenant, active: false);

        var response = await PutFormAsync(rig, Form(published: false, off), version: 0);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("support.form.organization_unavailable", await CodeOfAsync(response));
        Assert.Equal(0L, await CountFormVersionsAsync(rig));
    }

    // D-8: the form does not publish without its company.
    [Fact]
    public async Task PublishingWithoutACompanyIs422()
    {
        var rig = await RigAsync(SupportPermissions.SettingsManage);
        await PublishAllDocumentsAsync(rig);

        var response = await PutFormAsync(rig, Form(published: true, organizationCompanyId: null), version: 0);
        var read = await JsonAsync(await rig.Client.GetAsync(FormUrl(rig.Tenant), Ct));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("support.form.organization_required", await CodeOfAsync(response));
        Assert.Equal(["organization_required"], Blockers(read));
        Assert.Equal(0L, await CountFormVersionsAsync(rig));
    }

    [Fact]
    public async Task PublishingWithAnInactiveCompanyIs422()
    {
        var rig = await RigAsync(SupportPermissions.SettingsManage);
        await PublishAllDocumentsAsync(rig);
        var verde = await SeedCompanyAsync(rig.Factory, rig.Tenant);
        Assert.Equal(HttpStatusCode.OK, (await PutFormAsync(rig, Form(published: false, verde), version: 0)).StatusCode);
        await DeactivateCompanyAsync(rig.Factory, rig.Tenant, verde);

        var response = await PutFormAsync(rig, Form(published: true, verde), version: 1);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("support.form.organization_inactive", await CodeOfAsync(response));
        Assert.Equal(1L, await CountFormVersionsAsync(rig));
    }

    // Review Focus 8: a company deactivated after it was chosen is still shown, on the console and on the portal, and does not block
    // editing a published form; it only shows up in PublishBlockers.
    [Fact]
    public async Task AnInactiveChosenCompanyIsStillShownAndEditable()
    {
        var rig = await RigAsync(SupportPermissions.SettingsManage);
        await PublishAllDocumentsAsync(rig);
        var verde = await SeedCompanyAsync(rig.Factory, rig.Tenant);
        Assert.Equal(HttpStatusCode.OK, (await PutFormAsync(rig, Form(published: true, verde), version: 0)).StatusCode);
        await DeactivateCompanyAsync(rig.Factory, rig.Tenant, verde);

        var edit = await PutFormAsync(rig, Form(published: true, verde, introText: "Cuéntanos tu caso con detalle."), version: 1);

        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var body = await JsonAsync(edit);
        Assert.Equal(2, body.GetProperty("version").GetInt32());
        Assert.Equal(verde, body.GetProperty("organization").GetProperty("id").GetGuid());
        Assert.False(body.GetProperty("organization").GetProperty("isActive").GetBoolean());
        Assert.Contains("organization_inactive", Blockers(body));
        Assert.DoesNotContain(
            verde, body.GetProperty("organizationOptions").EnumerateArray().Select(option => option.GetProperty("id").GetGuid()));
        using var visitor = VisitorClient(rig.Factory);
        var form = await JsonAsync(await visitor.GetAsync(PublicUrl(await SlugOfAsync(rig.Factory, rig.Tenant), "/form"), Ct));
        Assert.Equal("Verde S.A.S.", form.GetProperty("organization").GetProperty("name").GetString());
    }

    // Review Focus 8: the FK keeps a chosen company from disappearing, also when only an old version of the form chose it. Companies
    // answers its existing code without knowing Support.
    [Fact]
    public async Task AChosenCompanyCannotBeDeleted()
    {
        var rig = await RigAsync(SupportPermissions.SettingsManage, CompaniesManage, CompaniesRead);
        var verde = await SeedCompanyAsync(rig.Factory, rig.Tenant, "Verde S.A.S.", "900123456");
        var azul = await SeedCompanyAsync(rig.Factory, rig.Tenant, "Azul Ltda.", "800111222");
        Assert.Equal(HttpStatusCode.OK, (await PutFormAsync(rig, Form(published: false, verde), version: 0)).StatusCode);

        var current = await rig.Client.DeleteAsync($"/api/v1/tenants/{rig.Tenant}/companies/{verde}", Ct);
        Assert.Equal(HttpStatusCode.OK, (await PutFormAsync(rig, Form(published: false, azul), version: 1)).StatusCode);
        var old = await rig.Client.DeleteAsync($"/api/v1/tenants/{rig.Tenant}/companies/{verde}", Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, current.StatusCode);
        Assert.Equal("companies.company.in_use", await CodeOfAsync(current));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, old.StatusCode);
        Assert.Equal("companies.company.in_use", await CodeOfAsync(old));
        Assert.Equal(1L, await ScalarAsync(
            rig.Factory, "SELECT count(*) FROM companies.companies WHERE id = @id", ("id", verde)));
    }

    // The FK only holds the companies some version chose.
    [Fact]
    public async Task AnUnchosenCompanyCanStillBeDeleted()
    {
        var rig = await RigAsync(SupportPermissions.SettingsManage, CompaniesManage, CompaniesRead);
        var verde = await SeedCompanyAsync(rig.Factory, rig.Tenant, "Verde S.A.S.", "900123456");
        var spare = await SeedCompanyAsync(rig.Factory, rig.Tenant, "Sobrante S.A.", "811222333");
        Assert.Equal(HttpStatusCode.OK, (await PutFormAsync(rig, Form(published: false, verde), version: 0)).StatusCode);

        var response = await rig.Client.DeleteAsync($"/api/v1/tenants/{rig.Tenant}/companies/{spare}", Ct);

        Assert.True(response.IsSuccessStatusCode, await BodyAsync(response));
        Assert.Equal(0L, await ScalarAsync(
            rig.Factory, "SELECT count(*) FROM companies.companies WHERE id = @id", ("id", spare)));
    }

    private static string FormUrl(string tenant) => $"{SupportUrl(tenant)}/settings/public-form";

    private static object Form(bool published, Guid? organizationCompanyId, string introText = "Cuéntanos tu caso.") => new
    {
        isPublished = published,
        showDocument = true,
        showAddress = true,
        showLandline = true,
        businessNameRequired = true,
        allowAttachments = true,
        accentColor = "#00B4FF",
        introText,
        successMessage = "Recibimos tu caso.",
        organizationCompanyId,
    };

    private static List<string?> Blockers(JsonElement body) =>
        body.GetProperty("publishBlockers").EnumerateArray().Select(item => item.GetString()).ToList();

    private static Task<HttpResponseMessage> PutFormAsync(Rig rig, object body, long? version) =>
        PutAsync(rig.Client, FormUrl(rig.Tenant), body, version);

    // Version 1 of the three documents, through the real sanitizer (SupportPublicHarness).
    private static async Task PublishAllDocumentsAsync(Rig rig)
    {
        foreach (var kind in SupportLegalDocumentKinds.All)
        {
            await PublishLegalDocumentAsync(rig.Factory, rig.Tenant, kind, 1, $"<p>{kind}, versión 1.</p>");
        }
    }

    private static Task<long> CountFormVersionsAsync(Rig rig) =>
        ScalarAsync(rig.Factory, "SELECT count(*) FROM support.form_versions WHERE tenant_id = @tenant", ("tenant", rig.TenantId));

    // The one who saves goes to created_by, a membership: the stub only carries the user, so its membership is seeded.
    private async Task<Rig> RigAsync(params string[] permissions)
    {
        var factory = await sharedHost.StartTestAsync();
        var tenant = NewTenantId();
        var subject = Guid.CreateVersion7().ToString();
        await SeedTenantAsync(factory, tenant);
        await SeedCallerMembershipAsync(factory, "Ana Configura", tenantId: tenant, subjectId: subject);
        return new Rig(factory, tenant, Guid.Parse(tenant), CreateClient(factory, subject, tenant, permissions));
    }

    private sealed record Rig(QepApiFactory Factory, string Tenant, Guid TenantId, HttpClient Client);
}
