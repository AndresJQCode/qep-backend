using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Support.Application;
using static Modules.Support.IntegrationTests.SupportApiHarness;
using static Modules.Support.IntegrationTests.SupportMessagingSeed;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// The three legal documents in the console (spec 2026-11-15 §6 and §7, plan L5 to L7, L21; contract «Consola»). Each kind is
/// versioned on its own and insert-only: the <c>If-Match</c> is the current version of the kind (0 without one) and publishing creates
/// the next. The HTML is stored as the sanitizer leaves it. They share the host: each test seeds its own tenant and member.
/// </summary>
public sealed class SupportLegalDocumentsApiTests(SharedSupportHost sharedHost) : IClassFixture<SharedSupportHost>
{
    private const string TenantName = "Support API Tests";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WithoutVersionsTheConsoleGetsTheThreeExamples()
    {
        var (_, client, tenant, _) = await NewTenantAsync(SupportPermissions.Read);

        var response = await client.GetAsync(DocumentsUrl(tenant), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        foreach (var (property, kind) in new[]
                 {
                     ("privacyNotice", "PrivacyNotice"), ("anonymousNotice", "AnonymousNotice"), ("terms", "Terms"),
                 })
        {
            var document = body.GetProperty(property);
            Assert.Equal(kind, document.GetProperty("kind").GetString());
            Assert.True(document.GetProperty("isExample").GetBoolean());
            Assert.Equal(0, document.GetProperty("version").GetInt32());
            Assert.StartsWith("<p>", document.GetProperty("contentHtml").GetString(), StringComparison.Ordinal);
            Assert.Equal(JsonValueKind.Null, document.GetProperty("createdAt").ValueKind);
            Assert.Equal(JsonValueKind.Null, document.GetProperty("createdBy").ValueKind);
            Assert.Equal(JsonValueKind.Null, document.GetProperty("authorizationQuestion").ValueKind);
            Assert.Equal(JsonValueKind.Null, document.GetProperty("policyUrl").ValueKind);
        }

        Assert.Equal(
            $"¿Autorizas a {TenantName} para el tratamiento de los datos personales suministrados en este formulario?",
            body.GetProperty("privacyNotice").GetProperty("defaultAuthorizationQuestion").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("terms").GetProperty("defaultAuthorizationQuestion").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("anonymousNotice").GetProperty("defaultAuthorizationQuestion").ValueKind);
        Assert.Equal(0L, await CountVersionsAsync(tenant));
    }

    [Fact]
    public async Task PublishingCreatesTheNextVersionPerKind()
    {
        var (_, client, tenant, member) = await NewTenantAsync(SupportPermissions.Read, SupportPermissions.SettingsManage);

        var firstTerms = await PublishAsync(client, tenant, "terms", new { contentHtml = "<p>Términos 1.</p>" }, version: 0);
        var secondTerms = await PublishAsync(client, tenant, "terms", new { contentHtml = "<p>Términos 2.</p>" }, version: 1);
        var privacy = await PublishAsync(
            client, tenant, "privacy-notice",
            new { contentHtml = "<p>Aviso.</p>", authorizationQuestion = " ¿Autorizas el tratamiento? ", policyUrl = "https://example.com/politica" },
            version: 0);

        Assert.Equal(HttpStatusCode.Created, firstTerms.StatusCode);
        Assert.Equal(HttpStatusCode.Created, secondTerms.StatusCode);
        Assert.Equal(HttpStatusCode.Created, privacy.StatusCode);
        Assert.Equal(2, (await JsonAsync(secondTerms)).GetProperty("version").GetInt32());
        Assert.Equal(1, (await JsonAsync(privacy)).GetProperty("version").GetInt32());

        var read = await JsonAsync(await client.GetAsync(DocumentsUrl(tenant), Ct));
        var terms = read.GetProperty("terms");
        var notice = read.GetProperty("privacyNotice");
        Assert.Equal(2, terms.GetProperty("version").GetInt32());
        Assert.False(terms.GetProperty("isExample").GetBoolean());
        Assert.Equal("<p>Términos 2.</p>", terms.GetProperty("contentHtml").GetString());
        Assert.Equal(member, terms.GetProperty("createdBy").GetProperty("id").GetGuid());
        Assert.Equal("Ana Publica", terms.GetProperty("createdBy").GetProperty("displayName").GetString());
        Assert.Equal(1, notice.GetProperty("version").GetInt32());
        Assert.Equal("¿Autorizas el tratamiento?", notice.GetProperty("authorizationQuestion").GetString());
        Assert.Equal("https://example.com/politica", notice.GetProperty("policyUrl").GetString());
        Assert.False(string.IsNullOrEmpty(notice.GetProperty("defaultAuthorizationQuestion").GetString()));
        Assert.True(read.GetProperty("anonymousNotice").GetProperty("isExample").GetBoolean());
        Assert.Equal(3L, await CountVersionsAsync(tenant));
    }

    [Fact]
    public async Task PublishingSetsTheETagAndReturnsTheStoredHtml()
    {
        var (_, client, tenant, _) = await NewTenantAsync(SupportPermissions.Read, SupportPermissions.SettingsManage);

        var response = await PublishAsync(
            client, tenant, "anonymous-notice", new { contentHtml = "<div><b>Aviso</b> <span>anónimo</span></div>" }, version: 0);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("\"1\"", response.Headers.ETag?.Tag);
        Assert.EndsWith(
            $"/api/v1/tenants/{tenant}/support/settings/legal-documents/anonymous-notice/versions/1",
            response.Headers.Location?.ToString(),
            StringComparison.Ordinal);
        var body = await JsonAsync(response);
        Assert.Equal("AnonymousNotice", body.GetProperty("kind").GetString());
        Assert.Equal("Aviso anónimo", body.GetProperty("contentHtml").GetString());
        Assert.Equal("Aviso anónimo", await StoredHtmlAsync(tenant, "AnonymousNotice", 1));
    }

    [Fact]
    public async Task TheStoredContentIsTheSanitizedOne()
    {
        var (_, client, tenant, _) = await NewTenantAsync(SupportPermissions.Read, SupportPermissions.SettingsManage);

        var response = await PublishAsync(
            client, tenant, "terms",
            new
            {
                contentHtml =
                    "<p onclick=\"alert(1)\">Hola</p><script>alert(1)</script><img src=x onerror=alert(1)>"
                    + "<a href=\"javascript:alert(1)\">x</a><a href=\"https://example.com\">y</a>",
            },
            version: 0);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var returned = (await JsonAsync(response)).GetProperty("contentHtml").GetString()!;
        var stored = (await StoredHtmlAsync(tenant, "Terms", 1))!;
        Assert.Equal(returned, stored);
        foreach (var dangerous in new[] { "onclick", "<script", "alert", "<img", "onerror", "javascript" })
        {
            Assert.DoesNotContain(dangerous, stored, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains(
            "<a href=\"https://example.com\" rel=\"noopener noreferrer nofollow\" target=\"_blank\">y</a>", stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContentWithoutVisibleTextIs422()
    {
        var (_, client, tenant, _) = await NewTenantAsync(SupportPermissions.Read, SupportPermissions.SettingsManage);

        var response = await PublishAsync(
            client, tenant, "terms", new { contentHtml = "<p>&nbsp;</p><script>alert(1)</script><br>" }, version: 0);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("support.legal_documents.content_empty", await CodeOfAsync(response));
        Assert.Equal(0L, await CountVersionsAsync(tenant));
    }

    // Under the raw cap (120 000) and over the stored one (60 000) once sanitized: only the handler can tell.
    [Fact]
    public async Task ContentTooLongAfterSanitizingIs422()
    {
        var (_, client, tenant, _) = await NewTenantAsync(SupportPermissions.Read, SupportPermissions.SettingsManage);

        var response = await PublishAsync(
            client, tenant, "terms", new { contentHtml = "<p>" + new string('a', 70_000) + "</p>" }, version: 0);
        var raw = await PublishAsync(
            client, tenant, "terms", new { contentHtml = "<p>" + new string('a', 120_000) + "</p>" }, version: 0);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("support.legal_documents.content_too_long", await CodeOfAsync(response));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, raw.StatusCode);
        Assert.Equal("validation.failed", await CodeOfAsync(raw));
        Assert.Equal(["ContentHtml"], await ErrorKeysAsync(raw));
        Assert.Equal(0L, await CountVersionsAsync(tenant));
    }

    [Fact]
    public async Task AStaleIfMatchIs412()
    {
        var (_, client, tenant, _) = await NewTenantAsync(SupportPermissions.Read, SupportPermissions.SettingsManage);
        await PublishAsync(client, tenant, "terms", new { contentHtml = "<p>Términos.</p>" }, version: 0);

        var stale = await PublishAsync(client, tenant, "terms", new { contentHtml = "<p>Otros.</p>" }, version: 0);
        var otherKind = await PublishAsync(client, tenant, "privacy-notice", new { contentHtml = "<p>Aviso.</p>" }, version: 1);

        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal("concurrency.conflict", await CodeOfAsync(stale));
        Assert.Equal(HttpStatusCode.PreconditionFailed, otherKind.StatusCode);
        Assert.Equal(1L, await CountVersionsAsync(tenant));
    }

    [Fact]
    public async Task WithoutIfMatchIs428()
    {
        var (_, client, tenant, _) = await NewTenantAsync(SupportPermissions.Read, SupportPermissions.SettingsManage);

        var response = await PublishAsync(client, tenant, "terms", new { contentHtml = "<p>Términos.</p>" }, version: null);

        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
        Assert.Equal("precondition.if_match_required", await CodeOfAsync(response));
    }

    [Theory]
    [InlineData("privacy_notice")]
    [InlineData("PrivacyNotice")]
    [InlineData("cookies")]
    public async Task AnUnknownKindIs404(string kind)
    {
        var (_, client, tenant, _) = await NewTenantAsync(SupportPermissions.Read, SupportPermissions.SettingsManage);

        var publish = await PublishAsync(client, tenant, kind, new { contentHtml = "<p>a</p>" }, version: 0);
        var history = await client.GetAsync($"{DocumentsUrl(tenant)}/{kind}/versions", Ct);
        var version = await client.GetAsync($"{DocumentsUrl(tenant)}/{kind}/versions/1", Ct);

        foreach (var response in new[] { publish, history, version })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("support.legal_documents.kind_not_found", await CodeOfAsync(response));
        }
    }

    [Fact]
    public async Task TheHistoryListsVersionsNewestFirst()
    {
        var (_, client, tenant, member) = await NewTenantAsync(SupportPermissions.Read, SupportPermissions.SettingsManage);
        var empty = await JsonAsync(await client.GetAsync($"{DocumentsUrl(tenant)}/terms/versions", Ct));
        await PublishAsync(client, tenant, "terms", new { contentHtml = "<p>Términos 1.</p>" }, version: 0);
        await PublishAsync(client, tenant, "terms", new { contentHtml = "<p>Términos 2.</p>" }, version: 1);
        await PublishAsync(client, tenant, "anonymous-notice", new { contentHtml = "<p>Aviso.</p>" }, version: 0);

        var response = await client.GetAsync($"{DocumentsUrl(tenant)}/terms/versions", Ct);

        Assert.Equal("Terms", empty.GetProperty("kind").GetString());
        Assert.Empty(empty.GetProperty("versions").EnumerateArray());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("Terms", body.GetProperty("kind").GetString());
        var versions = body.GetProperty("versions").EnumerateArray().ToList();
        Assert.Equal([2, 1], versions.Select(item => item.GetProperty("version").GetInt32()));
        Assert.All(versions, item => Assert.Equal(member, item.GetProperty("createdBy").GetProperty("id").GetGuid()));
        Assert.All(versions, item => Assert.False(item.TryGetProperty("contentHtml", out _)));
    }

    [Fact]
    public async Task AnOldVersionCanBeRead()
    {
        var (_, client, tenant, _) = await NewTenantAsync(SupportPermissions.Read, SupportPermissions.SettingsManage);
        await PublishAsync(
            client, tenant, "privacy-notice", new { contentHtml = "<p>Aviso 1.</p>", policyUrl = "https://example.com/v1" }, version: 0);
        await PublishAsync(client, tenant, "privacy-notice", new { contentHtml = "<p>Aviso 2.</p>" }, version: 1);

        var response = await client.GetAsync($"{DocumentsUrl(tenant)}/privacy-notice/versions/1", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal(1, body.GetProperty("version").GetInt32());
        Assert.False(body.GetProperty("isExample").GetBoolean());
        Assert.Equal("<p>Aviso 1.</p>", body.GetProperty("contentHtml").GetString());
        Assert.Equal("https://example.com/v1", body.GetProperty("policyUrl").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("defaultAuthorizationQuestion").GetString()));
    }

    [Fact]
    public async Task AMissingVersionIs404()
    {
        var (_, client, tenant, _) = await NewTenantAsync(SupportPermissions.Read, SupportPermissions.SettingsManage);
        await PublishAsync(client, tenant, "terms", new { contentHtml = "<p>Términos.</p>" }, version: 0);

        var missing = await client.GetAsync($"{DocumentsUrl(tenant)}/terms/versions/2", Ct);
        var zero = await client.GetAsync($"{DocumentsUrl(tenant)}/terms/versions/0", Ct);
        var otherKind = await client.GetAsync($"{DocumentsUrl(tenant)}/anonymous-notice/versions/1", Ct);

        foreach (var response in new[] { missing, zero, otherKind })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("support.legal_documents.version_not_found", await CodeOfAsync(response));
        }
    }

    [Fact]
    public async Task AReaderCanSeeButNotPublish()
    {
        var (factory, _, tenant, _) = await NewTenantAsync(SupportPermissions.Read);
        var reader = CreateReader(factory, tenant);
        var otherTenant = NewTenantId();
        await SeedTenantAsync(factory, otherTenant);

        var read = await reader.GetAsync(DocumentsUrl(tenant), Ct);
        var history = await reader.GetAsync($"{DocumentsUrl(tenant)}/terms/versions", Ct);
        var publish = await PublishAsync(reader, tenant, "terms", new { contentHtml = "<p>Términos.</p>" }, version: 0);
        var foreign = await reader.GetAsync(DocumentsUrl(otherTenant), Ct);

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, publish.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.Equal(0L, await CountVersionsAsync(tenant));
    }

    [Fact]
    public async Task PublishingWritesAuditWithoutContent()
    {
        var (factory, client, tenant, _) = await NewTenantAsync(SupportPermissions.Read, SupportPermissions.SettingsManage);
        const string Secret = "TextoQueNoVaALaAuditoria";

        var privacy = await JsonAsync(await PublishAsync(
            client, tenant, "privacy-notice", new { contentHtml = $"<p>{Secret}</p>", authorizationQuestion = "¿Autorizas?" }, version: 0));
        var terms = await JsonAsync(await PublishAsync(client, tenant, "terms", new { contentHtml = $"<p>{Secret}</p>" }, version: 0));

        var privacyId = await VersionIdAsync(tenant, "PrivacyNotice", 1);
        var termsId = await VersionIdAsync(tenant, "Terms", 1);
        Assert.Equal(1, privacy.GetProperty("version").GetInt32());
        Assert.Equal(1, terms.GetProperty("version").GetInt32());
        Assert.Equal(1, await CountAuditEntriesAsync(factory, SupportAuditActions.LegalDocumentPublished, privacyId));
        Assert.Equal(1, await CountAuditEntriesAsync(factory, SupportAuditActions.LegalDocumentPublished, termsId));
        Assert.Equal(
            "[\"kind\", \"contentHtml\", \"authorizationQuestion\", \"policyUrl\"]",
            await ChangedFieldsAsync(factory, privacyId));
        Assert.Equal("[\"kind\", \"contentHtml\"]", await ChangedFieldsAsync(factory, termsId));
        Assert.Equal(0L, await ScalarAsync(
            factory,
            "SELECT count(*) FROM audit.entries WHERE tenant_id = @tenant AND changed_fields::text LIKE @secret",
            ("tenant", Guid.Parse(tenant)), ("secret", $"%{Secret}%")));
    }

    [Fact]
    public async Task TheOldLegalTextsRoutesAreGone()
    {
        var (_, client, tenant, _) = await NewTenantAsync(SupportPermissions.Read, SupportPermissions.SettingsManage);
        var url = $"{SupportUrl(tenant)}/settings/legal-texts";

        var get = await client.GetAsync(url, Ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new { authorizationText = "Autorizo.", policyUrl = "https://example.com/politica" }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", "\"0\"");
        var post = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
    }

    private static string DocumentsUrl(string tenant) => $"{SupportUrl(tenant)}/settings/legal-documents";

    private static async Task<HttpResponseMessage> PublishAsync(HttpClient client, string tenant, string kind, object body, long? version)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{DocumentsUrl(tenant)}/{kind}") { Content = JsonContent.Create(body) };
        if (version is { } value)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{value}\"");
        }

        return await client.SendAsync(request, Ct);
    }

    private async Task<long> CountVersionsAsync(string tenant) =>
        await ScalarAsync(
            await sharedHost.StartTestAsync(),
            "SELECT count(*) FROM support.legal_document_versions WHERE tenant_id = @tenant",
            ("tenant", Guid.Parse(tenant)));

    private async Task<string?> StoredHtmlAsync(string tenant, string kind, int version) =>
        await TextAsync(
            await sharedHost.StartTestAsync(),
            "SELECT content_html FROM support.legal_document_versions WHERE tenant_id = @tenant AND kind = @kind AND version = @version",
            ("tenant", Guid.Parse(tenant)), ("kind", kind), ("version", version));

    private async Task<string> VersionIdAsync(string tenant, string kind, int version) =>
        (await TextAsync(
            await sharedHost.StartTestAsync(),
            "SELECT id::text FROM support.legal_document_versions WHERE tenant_id = @tenant AND kind = @kind AND version = @version",
            ("tenant", Guid.Parse(tenant)), ("kind", kind), ("version", version)))!;

    private static Task<string?> ChangedFieldsAsync(QepApiFactory factory, string resourceId) =>
        TextAsync(
            factory,
            "SELECT changed_fields::text FROM audit.entries WHERE action = @action AND resource_id = @id",
            ("action", SupportAuditActions.LegalDocumentPublished), ("id", resourceId));

    // The publisher is created_by, a membership: the stub only brings the user, so its membership is seeded.
    private async Task<(QepApiFactory Factory, HttpClient Client, string Tenant, Guid Member)> NewTenantAsync(params string[] permissions)
    {
        var factory = await sharedHost.StartTestAsync();
        var tenant = NewTenantId();
        var subject = Guid.CreateVersion7().ToString();
        await SeedTenantAsync(factory, tenant);
        var member = await SeedCallerMembershipAsync(factory, "Ana Publica", tenantId: tenant, subjectId: subject);
        return (factory, CreateClient(factory, subject, tenant, permissions), tenant, member);
    }
}
