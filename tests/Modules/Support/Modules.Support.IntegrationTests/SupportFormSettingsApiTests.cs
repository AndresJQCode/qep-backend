using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Support.Application;
using static Modules.Support.IntegrationTests.SupportApiHarness;
using static Modules.Support.IntegrationTests.SupportMessagingSeed;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// La configuración del formulario público y los textos legales en la consola (plan fase 3, Task 3; F14 a F16). Las dos son
/// versionadas e insert-only: el <c>If-Match</c> es el número de la versión vigente (0 sin filas) y guardar crea la siguiente.
/// Comparten host: cada prueba siembra su tenant y su miembro, y sólo lee lo suyo.
/// </summary>
public sealed class SupportFormSettingsApiTests(SharedSupportHost sharedHost) : IClassFixture<SharedSupportHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly object PublishedForm = new
    {
        isPublished = true,
        showDocument = true,
        showAddress = false,
        showLandline = true,
        businessNameRequired = false,
        allowAttachments = true,
        accentColor = "#00B4FF",
        introText = "Cuéntanos tu caso.",
        successMessage = "Recibimos tu caso.",
    };

    private static readonly object LegalTexts = new
    {
        authorizationText = "Autorizo el tratamiento de mis datos personales.",
        policyText = (string?)null,
        policyUrl = "https://example.com/politica",
        termsText = "Términos del servicio.",
    };

    [Fact]
    public async Task WithoutRowsTheFormIsUnpublishedWithDefaultsAndBlockers()
    {
        var (_, client, tenant) = await NewTenantAsync(SupportPermissions.Read);

        var response = await client.GetAsync(FormUrl(tenant), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal(0, body.GetProperty("version").GetInt32());
        Assert.False(body.GetProperty("isPublished").GetBoolean());
        Assert.True(body.GetProperty("showDocument").GetBoolean());
        Assert.True(body.GetProperty("showAddress").GetBoolean());
        Assert.True(body.GetProperty("showLandline").GetBoolean());
        Assert.True(body.GetProperty("businessNameRequired").GetBoolean());
        Assert.False(body.GetProperty("allowAttachments").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("accentColor").ValueKind);
        Assert.EndsWith("/" + await SlugAsync(tenant), body.GetProperty("publicUrl").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("https://", body.GetProperty("publicUrl").GetString(), StringComparison.Ordinal);
        Assert.Equal(["legal_texts_required"], Blockers(body));
        Assert.Equal("\"0\"", response.Headers.ETag?.Tag);
    }

    [Fact]
    public async Task PublishingWithoutLegalTextsIs422()
    {
        var (_, client, tenant) = await NewTenantAsync(SupportPermissions.SettingsManage);

        var response = await PutFormAsync(client, tenant, PublishedForm, version: 0);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("support.form.legal_texts_required", await CodeOfAsync(response));
        Assert.Equal(0L, await CountFormVersionsAsync(tenant));
    }

    // El POST de los textos aprovisiona los tipos por defecto (todos publicados); después se despublican a mano.
    [Fact]
    public async Task WithoutPublishedTypesTheFormCannotBePublished()
    {
        var (factory, client, tenant) = await NewTenantAsync(SupportPermissions.SettingsManage);
        Assert.Equal(HttpStatusCode.Created, (await PostLegalAsync(client, tenant, LegalTexts, version: 0)).StatusCode);
        await ExecuteAsync(
            factory, "UPDATE support.ticket_types SET is_published = false WHERE tenant_id = @tenant", ("tenant", Guid.Parse(tenant)));

        var read = await JsonAsync(await client.GetAsync(FormUrl(tenant), Ct));
        var publish = await PutFormAsync(client, tenant, PublishedForm, version: 0);
        var saveUnpublished = await PutFormAsync(
            client, tenant,
            new
            {
                isPublished = false, showDocument = false, showAddress = true, showLandline = true, businessNameRequired = true,
                allowAttachments = false,
            },
            version: 0);

        Assert.Equal(["no_published_types"], Blockers(read));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, publish.StatusCode);
        Assert.Equal("support.form.no_published_types", await CodeOfAsync(publish));
        Assert.Equal(HttpStatusCode.OK, saveUnpublished.StatusCode);
        Assert.Equal(1, (await JsonAsync(saveUnpublished)).GetProperty("version").GetInt32());
    }

    // El 422 sólo frena el paso a publicado: un formulario ya publicado al que después le despublicaron el último tipo se sigue
    // pudiendo editar, y el motivo queda en PublishBlockers.
    [Fact]
    public async Task EditingAPublishedFormAfterItsLastTypeWasUnpublishedSucceeds()
    {
        var (factory, client, tenant) = await NewTenantAsync(SupportPermissions.SettingsManage);
        Assert.Equal(HttpStatusCode.Created, (await PostLegalAsync(client, tenant, LegalTexts, version: 0)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutFormAsync(client, tenant, PublishedForm, version: 0)).StatusCode);
        await ExecuteAsync(
            factory, "UPDATE support.ticket_types SET is_published = false WHERE tenant_id = @tenant", ("tenant", Guid.Parse(tenant)));

        var edit = await PutFormAsync(
            client, tenant,
            new
            {
                isPublished = true,
                showDocument = true,
                showAddress = false,
                showLandline = true,
                businessNameRequired = false,
                allowAttachments = true,
                accentColor = "#00B4FF",
                introText = "Cuéntanos tu caso con detalle.",
                successMessage = "Recibimos tu caso.",
            },
            version: 1);

        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var body = await JsonAsync(edit);
        Assert.Equal(2, body.GetProperty("version").GetInt32());
        Assert.True(body.GetProperty("isPublished").GetBoolean());
        Assert.Equal(["no_published_types"], Blockers(body));
        Assert.Equal(2L, await CountFormVersionsAsync(tenant));
    }

    [Fact]
    public async Task SavingCreatesTheNextVersionAndKeepsThePrevious()
    {
        var (factory, client, tenant) = await NewTenantAsync(SupportPermissions.SettingsManage);
        Assert.Equal(HttpStatusCode.Created, (await PostLegalAsync(client, tenant, LegalTexts, version: 0)).StatusCode);

        var first = await PutFormAsync(client, tenant, PublishedForm, version: 0);
        var second = await PutFormAsync(
            client, tenant,
            new
            {
                isPublished = true,
                showDocument = false,
                showAddress = true,
                showLandline = false,
                businessNameRequired = true,
                allowAttachments = false,
                accentColor = (string?)null,
                introText = (string?)null,
                successMessage = "Gracias por escribirnos.",
            },
            version: 1);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.True((await JsonAsync(first)).GetProperty("allowAttachments").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("\"2\"", second.Headers.ETag?.Tag);
        var body = await JsonAsync(second);
        Assert.Equal(2, body.GetProperty("version").GetInt32());
        Assert.True(body.GetProperty("isPublished").GetBoolean());
        Assert.False(body.GetProperty("showDocument").GetBoolean());
        Assert.False(body.GetProperty("allowAttachments").GetBoolean());
        Assert.Equal("true", await TextAsync(
            factory, "SELECT allow_attachments::text FROM support.form_versions WHERE tenant_id = @tenant AND version = 1",
            ("tenant", Guid.Parse(tenant))));
        Assert.Equal("Gracias por escribirnos.", body.GetProperty("successMessage").GetString());
        Assert.Empty(Blockers(body));
        Assert.Equal(2L, await CountFormVersionsAsync(tenant));
        Assert.Equal("#00B4FF", await TextAsync(
            factory, "SELECT accent_color FROM support.form_versions WHERE tenant_id = @tenant AND version = 1", ("tenant", Guid.Parse(tenant))));
        var read = await JsonAsync(await client.GetAsync(FormUrl(tenant), Ct));
        Assert.Equal(2, read.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task SavingTheSameValuesCreatesNoVersion()
    {
        var (factory, client, tenant) = await NewTenantAsync(SupportPermissions.SettingsManage);
        await PostLegalAsync(client, tenant, LegalTexts, version: 0);
        await PutFormAsync(client, tenant, PublishedForm, version: 0);

        var again = await PutFormAsync(client, tenant, PublishedForm, version: 1);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(1, (await JsonAsync(again)).GetProperty("version").GetInt32());
        Assert.Equal(1L, await CountFormVersionsAsync(tenant));
        Assert.Equal(1, await CountAuditEntriesAsync(factory, SupportAuditActions.FormUpdated, tenant));
    }

    [Fact]
    public async Task AStaleIfMatchIs412()
    {
        var (_, client, tenant) = await NewTenantAsync(SupportPermissions.SettingsManage);
        await PostLegalAsync(client, tenant, LegalTexts, version: 0);
        await PutFormAsync(client, tenant, PublishedForm, version: 0);

        var staleForm = await PutFormAsync(client, tenant, UnpublishedForm, version: 0);
        var staleLegal = await PostLegalAsync(client, tenant, LegalTexts, version: 0);

        Assert.Equal(HttpStatusCode.PreconditionFailed, staleForm.StatusCode);
        Assert.Equal("concurrency.conflict", await CodeOfAsync(staleForm));
        Assert.Equal(HttpStatusCode.PreconditionFailed, staleLegal.StatusCode);
        Assert.Equal(1L, await CountFormVersionsAsync(tenant));
    }

    [Fact]
    public async Task WithoutIfMatchIs428()
    {
        var (_, client, tenant) = await NewTenantAsync(SupportPermissions.SettingsManage);

        var form = await PutFormAsync(client, tenant, PublishedForm, version: null);
        var legal = await PostLegalAsync(client, tenant, LegalTexts, version: null);

        Assert.Equal(HttpStatusCode.PreconditionRequired, form.StatusCode);
        Assert.Equal("precondition.if_match_required", await CodeOfAsync(form));
        Assert.Equal(HttpStatusCode.PreconditionRequired, legal.StatusCode);
        Assert.Equal("precondition.if_match_required", await CodeOfAsync(legal));
    }

    [Fact]
    public async Task LegalTextsGetReturnsTheExampleUntilPublished()
    {
        var (_, client, tenant) = await NewTenantAsync(SupportPermissions.Read);

        var response = await client.GetAsync(LegalUrl(tenant), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.True(body.GetProperty("isExample").GetBoolean());
        Assert.Equal(0, body.GetProperty("version").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("authorizationText").GetString()));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("createdBy").ValueKind);
        Assert.Equal(0L, await CountLegalVersionsAsync(tenant));
    }

    [Fact]
    public async Task PublishingLegalTextsTwiceKeepsBothVersions()
    {
        var (_, client, tenant, member) = await NewTenantWithMemberAsync(SupportPermissions.SettingsManage);

        var first = await PostLegalAsync(client, tenant, LegalTexts, version: 0);
        var second = await PostLegalAsync(
            client, tenant,
            new { authorizationText = "Autorizo, versión 2.", policyText = "Política v2", policyUrl = (string?)null, termsText = (string?)null },
            version: 1);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var body = await JsonAsync(second);
        Assert.Equal(2, body.GetProperty("version").GetInt32());
        Assert.False(body.GetProperty("isExample").GetBoolean());
        Assert.Equal("Política v2", body.GetProperty("policyText").GetString());
        Assert.Equal(member, body.GetProperty("createdBy").GetProperty("id").GetGuid());
        Assert.Equal(2L, await CountLegalVersionsAsync(tenant));
        var read = await JsonAsync(await client.GetAsync(LegalUrl(tenant), Ct));
        Assert.Equal(2, read.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task LegalTextsWithoutPolicyAre422()
    {
        var (_, client, tenant) = await NewTenantAsync(SupportPermissions.SettingsManage);

        var response = await PostLegalAsync(
            client, tenant, new { authorizationText = "Autorizo.", policyText = " ", policyUrl = (string?)null, termsText = (string?)null },
            version: 0);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("support.legal_texts.policy_required", await CodeOfAsync(response));
        Assert.Equal(0L, await CountLegalVersionsAsync(tenant));
    }

    [Fact]
    public async Task AReaderCanSeeButNotSave()
    {
        var (_, client, tenant) = await NewTenantAsync(SupportPermissions.Read);

        var form = await client.GetAsync(FormUrl(tenant), Ct);
        var legal = await client.GetAsync(LegalUrl(tenant), Ct);
        var saveForm = await PutFormAsync(client, tenant, PublishedForm, version: 0);
        var saveLegal = await PostLegalAsync(client, tenant, LegalTexts, version: 0);

        Assert.Equal(HttpStatusCode.OK, form.StatusCode);
        Assert.Equal(HttpStatusCode.OK, legal.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, saveForm.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, saveLegal.StatusCode);
    }

    [Fact]
    public async Task SavingWritesAudit()
    {
        var (factory, client, tenant) = await NewTenantAsync(SupportPermissions.SettingsManage);

        await PostLegalAsync(client, tenant, LegalTexts, version: 0);
        await PutFormAsync(client, tenant, PublishedForm, version: 0);

        Assert.Equal(1, await CountAuditEntriesAsync(factory, SupportAuditActions.LegalTextsPublished, tenant));
        Assert.Equal(1, await CountAuditEntriesAsync(factory, SupportAuditActions.FormUpdated, tenant));
    }

    private static readonly object UnpublishedForm = new
    {
        isPublished = false,
        showDocument = true,
        showAddress = true,
        showLandline = true,
        businessNameRequired = true,
        allowAttachments = false,
        accentColor = (string?)null,
        introText = (string?)null,
        successMessage = (string?)null,
    };

    private static string FormUrl(string tenant) => $"{SupportUrl(tenant)}/settings/public-form";

    private static string LegalUrl(string tenant) => $"{SupportUrl(tenant)}/settings/legal-texts";

    private static List<string?> Blockers(JsonElement body) =>
        body.GetProperty("publishBlockers").EnumerateArray().Select(item => item.GetString()).ToList();

    private static Task<HttpResponseMessage> PutFormAsync(HttpClient client, string tenant, object body, long? version) =>
        PutAsync(client, FormUrl(tenant), body, version);

    private static async Task<HttpResponseMessage> PostLegalAsync(HttpClient client, string tenant, object body, long? version)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, LegalUrl(tenant)) { Content = JsonContent.Create(body) };
        if (version is { } value)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{value}\"");
        }

        return await client.SendAsync(request, Ct);
    }

    private async Task<string?> SlugAsync(string tenant) =>
        await TextAsync(await sharedHost.StartTestAsync(), "SELECT slug FROM tenancy.tenants WHERE id = @id", ("id", Guid.Parse(tenant)));

    private async Task<long> CountFormVersionsAsync(string tenant) =>
        await ScalarAsync(
            await sharedHost.StartTestAsync(), "SELECT count(*) FROM support.form_versions WHERE tenant_id = @tenant",
            ("tenant", Guid.Parse(tenant)));

    private async Task<long> CountLegalVersionsAsync(string tenant) =>
        await ScalarAsync(
            await sharedHost.StartTestAsync(), "SELECT count(*) FROM support.legal_text_versions WHERE tenant_id = @tenant",
            ("tenant", Guid.Parse(tenant)));

    private async Task<(QepApiFactory Factory, HttpClient Client, string Tenant)> NewTenantAsync(params string[] permissions)
    {
        var (factory, client, tenant, _) = await NewTenantWithMemberAsync(permissions);
        return (factory, client, tenant);
    }

    // El que guarda queda en created_by, que es una membresía: el stub sólo trae el usuario, así que se siembra la suya.
    private async Task<(QepApiFactory Factory, HttpClient Client, string Tenant, Guid Member)> NewTenantWithMemberAsync(
        params string[] permissions)
    {
        var factory = await sharedHost.StartTestAsync();
        var tenant = NewTenantId();
        var subject = Guid.CreateVersion7().ToString();
        await SeedTenantAsync(factory, tenant);
        var member = await SeedCallerMembershipAsync(factory, "Ana Configura", tenantId: tenant, subjectId: subject);
        return (factory, CreateClient(factory, subject, tenant, permissions), tenant, member);
    }
}
