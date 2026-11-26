using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Support.Application;
using static Modules.Support.IntegrationTests.SupportApiHarness;
using static Modules.Support.IntegrationTests.SupportMessagingSeed;
using static Modules.Support.IntegrationTests.SupportPublicHarness;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// La configuración del formulario público en la consola (plan fase 3, Task 3; F14 y F15). Versionada e insert-only: el
/// <c>If-Match</c> es el número de la versión vigente (0 sin filas) y guardar crea la siguiente. Publicar exige los tres documentos
/// legales (spec 2026-11-15 §7, plan L18); los documentos tienen su propia clase (<c>SupportLegalDocumentsApiTests</c>). Comparten
/// host: cada prueba siembra su tenant y su miembro, y sólo lee lo suyo.
/// </summary>
public sealed class SupportFormSettingsApiTests(SharedSupportHost sharedHost) : IClassFixture<SharedSupportHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Spec 2026-11-15 §15 D-8: publishing needs the company that answers; the tests that publish choose one of the tenant.
    private static object PublishedForm(Guid? organizationCompanyId = null) => new
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
        organizationCompanyId,
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
        // Owner, 2026-11-15 (E2): los adjuntos vienen prendidos.
        Assert.True(body.GetProperty("allowAttachments").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("accentColor").ValueKind);
        Assert.EndsWith("/" + await SlugAsync(tenant), body.GetProperty("publicUrl").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("https://", body.GetProperty("publicUrl").GetString(), StringComparison.Ordinal);
        Assert.Equal(["privacy_notice_required", "anonymous_notice_required", "terms_required", "organization_required"], Blockers(body));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("organization").ValueKind);
        Assert.Empty(body.GetProperty("organizationOptions").EnumerateArray());
        Assert.Equal("\"0\"", response.Headers.ETag?.Tag);
    }

    // Spec 2026-11-15 §7: the three documents are required; the code does not change and the list says which one is missing.
    [Fact]
    public async Task PublishingWithAMissingDocumentIs422()
    {
        var (_, client, tenant) = await NewTenantAsync(SupportPermissions.SettingsManage);
        await PublishDocumentAsync(client, tenant, "privacy-notice");
        await PublishDocumentAsync(client, tenant, "terms");

        var response = await PutFormAsync(client, tenant, PublishedForm(), version: 0);
        var read = await JsonAsync(await client.GetAsync(FormUrl(tenant), Ct));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("support.form.legal_texts_required", await CodeOfAsync(response));
        Assert.Equal(["anonymous_notice_required", "organization_required"], Blockers(read));
        Assert.Equal(0L, await CountFormVersionsAsync(tenant));
    }

    // Publicar un documento aprovisiona los tipos por defecto (todos publicados); después se despublican a mano.
    [Fact]
    public async Task WithoutPublishedTypesTheFormCannotBePublished()
    {
        var (factory, client, tenant) = await NewTenantAsync(SupportPermissions.SettingsManage);
        await PublishAllDocumentsAsync(client, tenant);
        await ExecuteAsync(
            factory, "UPDATE support.ticket_types SET is_published = false WHERE tenant_id = @tenant", ("tenant", Guid.Parse(tenant)));

        var company = await SeedCompanyAsync(factory, tenant);
        var read = await JsonAsync(await client.GetAsync(FormUrl(tenant), Ct));
        var publish = await PutFormAsync(client, tenant, PublishedForm(company), version: 0);
        var saveUnpublished = await PutFormAsync(
            client, tenant,
            new
            {
                isPublished = false,
                showDocument = false,
                showAddress = true,
                showLandline = true,
                businessNameRequired = true,
                allowAttachments = false,
            },
            version: 0);

        Assert.Equal(["organization_required", "no_published_types"], Blockers(read));
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
        await PublishAllDocumentsAsync(client, tenant);
        var company = await SeedCompanyAsync(factory, tenant);
        Assert.Equal(HttpStatusCode.OK, (await PutFormAsync(client, tenant, PublishedForm(company), version: 0)).StatusCode);
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
                organizationCompanyId = company,
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
        await PublishAllDocumentsAsync(client, tenant);
        var company = await SeedCompanyAsync(factory, tenant);

        var first = await PutFormAsync(client, tenant, PublishedForm(company), version: 0);
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
                organizationCompanyId = company,
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
        // Owner, 2026-11-15 (E2): con el default de la columna en true, un false tiene que llegar a la base (HasSentinel(true)).
        Assert.Equal("false", await TextAsync(
            factory, "SELECT allow_attachments::text FROM support.form_versions WHERE tenant_id = @tenant AND version = 2",
            ("tenant", Guid.Parse(tenant))));
        Assert.False((await JsonAsync(await client.GetAsync(FormUrl(tenant), Ct))).GetProperty("allowAttachments").GetBoolean());
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
        await PublishAllDocumentsAsync(client, tenant);
        var company = await SeedCompanyAsync(factory, tenant);
        await PutFormAsync(client, tenant, PublishedForm(company), version: 0);

        var again = await PutFormAsync(client, tenant, PublishedForm(company), version: 1);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(1, (await JsonAsync(again)).GetProperty("version").GetInt32());
        Assert.Equal(1L, await CountFormVersionsAsync(tenant));
        Assert.Equal(1, await CountAuditEntriesAsync(factory, SupportAuditActions.FormUpdated, tenant));
    }

    [Fact]
    public async Task AStaleIfMatchIs412()
    {
        var (factory, client, tenant) = await NewTenantAsync(SupportPermissions.SettingsManage);
        await PublishAllDocumentsAsync(client, tenant);
        await PutFormAsync(client, tenant, PublishedForm(await SeedCompanyAsync(factory, tenant)), version: 0);

        var staleForm = await PutFormAsync(client, tenant, UnpublishedForm, version: 0);

        Assert.Equal(HttpStatusCode.PreconditionFailed, staleForm.StatusCode);
        Assert.Equal("concurrency.conflict", await CodeOfAsync(staleForm));
        Assert.Equal(1L, await CountFormVersionsAsync(tenant));
    }

    [Fact]
    public async Task WithoutIfMatchIs428()
    {
        var (_, client, tenant) = await NewTenantAsync(SupportPermissions.SettingsManage);

        var form = await PutFormAsync(client, tenant, PublishedForm(), version: null);

        Assert.Equal(HttpStatusCode.PreconditionRequired, form.StatusCode);
        Assert.Equal("precondition.if_match_required", await CodeOfAsync(form));
    }

    [Fact]
    public async Task AReaderCanSeeButNotSave()
    {
        var (_, client, tenant) = await NewTenantAsync(SupportPermissions.Read);

        var form = await client.GetAsync(FormUrl(tenant), Ct);
        var saveForm = await PutFormAsync(client, tenant, PublishedForm(), version: 0);

        Assert.Equal(HttpStatusCode.OK, form.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, saveForm.StatusCode);
    }

    [Fact]
    public async Task SavingWritesAudit()
    {
        var (factory, client, tenant) = await NewTenantAsync(SupportPermissions.SettingsManage);

        await PublishAllDocumentsAsync(client, tenant);
        await PutFormAsync(client, tenant, PublishedForm(await SeedCompanyAsync(factory, tenant)), version: 0);

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

    private static string DocumentUrl(string tenant, string kind) => $"{SupportUrl(tenant)}/settings/legal-documents/{kind}";

    private static List<string?> Blockers(JsonElement body) =>
        body.GetProperty("publishBlockers").EnumerateArray().Select(item => item.GetString()).ToList();

    private static Task<HttpResponseMessage> PutFormAsync(HttpClient client, string tenant, object body, long? version) =>
        PutAsync(client, FormUrl(tenant), body, version);

    // Version 1 of a document, from the console: the publication also provisions the default types (all published).
    private static async Task PublishDocumentAsync(HttpClient client, string tenant, string kind)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, DocumentUrl(tenant, kind))
        {
            Content = JsonContent.Create(new { contentHtml = $"<p>Documento {kind}.</p>" }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", "\"0\"");
        var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task PublishAllDocumentsAsync(HttpClient client, string tenant)
    {
        foreach (var kind in new[] { "privacy-notice", "anonymous-notice", "terms" })
        {
            await PublishDocumentAsync(client, tenant, kind);
        }
    }

    private async Task<string?> SlugAsync(string tenant) =>
        await TextAsync(await sharedHost.StartTestAsync(), "SELECT slug FROM tenancy.tenants WHERE id = @id", ("id", Guid.Parse(tenant)));

    private async Task<long> CountFormVersionsAsync(string tenant) =>
        await ScalarAsync(
            await sharedHost.StartTestAsync(), "SELECT count(*) FROM support.form_versions WHERE tenant_id = @tenant",
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
