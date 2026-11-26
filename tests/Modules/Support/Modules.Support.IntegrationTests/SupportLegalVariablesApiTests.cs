using System.Net;
using System.Text.Json;
using Modules.Support.Domain;
using static Modules.Support.IntegrationTests.SupportApiHarness;
using static Modules.Support.IntegrationTests.SupportMessagingSeed;
using static Modules.Support.IntegrationTests.SupportPublicHarness;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// Las variables de los documentos legales en el portal (spec 2026-11-16 §3): <c>GET /form</c> sirve los tres documentos y la pregunta
/// ya resueltos con la empresa que responde y el nombre del tenant, y la foto de cada aceptación guarda lo resuelto, que es lo que la
/// persona vio. Cada prueba siembra su tenant y llega desde su propia IP.
/// </summary>
public sealed class SupportLegalVariablesApiTests(SupportPublicHost host) : IClassFixture<SupportPublicHost>
{
    // El nombre que siembra SeedTenantAsync y una razón social con & para ver la codificación en el HTML y su ausencia en la pregunta.
    private const string TenantName = "Support API Tests";

    private const string Company = "Verde & Cía S.A.S.";

    private const string PrivacyTemplate =
        "<p>{{empresa}} ({{ tipo_documento }} {{documento_identificacion}}) atiende a los clientes de {{nombre_comercial}}.</p>";

    private const string Question = "¿Autorizas a {{empresa}} a tratar tus datos?";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheFormServesTheDocumentsResolved()
    {
        var rig = await RigAsync();

        var response = await rig.Client.GetAsync(PublicUrl(rig.Slug, "/form"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var legal = (await JsonAsync(response)).GetProperty("legal");
        var privacy = legal.GetProperty("privacyNotice");
        Assert.Equal(2, privacy.GetProperty("version").GetInt32());
        Assert.Equal(
            $"<p>Verde &amp; Cía S.A.S. (NIT 900123456) atiende a los clientes de {TenantName}.</p>",
            privacy.GetProperty("contentHtml").GetString());
        // V-2: texto plano, sin codificar.
        Assert.Equal($"¿Autorizas a {Company} a tratar tus datos?", privacy.GetProperty("authorizationQuestion").GetString());
        Assert.Equal("<p>Aviso anónimo de Verde &amp; Cía S.A.S.</p>", legal.GetProperty("anonymousNotice").GetProperty("contentHtml").GetString());
        Assert.Equal($"<p>Términos de {TenantName}, NIT 900123456.</p>", legal.GetProperty("terms").GetProperty("contentHtml").GetString());
    }

    // Review Focus 3: la foto guarda lo resuelto; la versión publicada sigue con sus tokens.
    [Fact]
    public async Task FilingStoresTheResolvedText()
    {
        var rig = await RigAsync();

        var named = await PostJsonAsync(
            rig.Client, PublicUrl(rig.Slug, "/tickets"),
            NamedBody(rig.TypeId, rig.CityId, $"ana-{Guid.CreateVersion7():N}@example.com", privacyNoticeVersion: 2, termsVersion: 2));
        Assert.True(named.StatusCode == HttpStatusCode.Created, await BodyAsync(named));
        var anonymous = await PostJsonAsync(
            VisitorClient(rig.Factory), PublicUrl(rig.Slug, "/tickets"), AnonymousBody(rig.TypeId, anonymousNoticeVersion: 2, termsVersion: 2));
        Assert.True(anonymous.StatusCode == HttpStatusCode.Created, await BodyAsync(anonymous));

        Assert.Equal(
            $"<p>Verde &amp; Cía S.A.S. (NIT 900123456) atiende a los clientes de {TenantName}.</p>",
            await AcceptedAsync(rig, "PrivacyNotice", "content_html"));
        Assert.Equal($"¿Autorizas a {Company} a tratar tus datos?", await AcceptedAsync(rig, "PrivacyNotice", "authorization_question"));
        Assert.Equal("<p>Aviso anónimo de Verde &amp; Cía S.A.S.</p>", await AcceptedAsync(rig, "AnonymousNotice", "content_html"));
        Assert.Equal(2L, await ScalarAsync(
            rig.Factory,
            "SELECT count(*) FROM support.legal_acceptances WHERE tenant_id = @tenant AND kind = 'Terms' AND content_html = @html",
            ("tenant", Guid.Parse(rig.Tenant)), ("html", $"<p>Términos de {TenantName}, NIT 900123456.</p>")));
        Assert.Equal(PrivacyTemplate, await TextAsync(
            rig.Factory,
            "SELECT content_html FROM support.legal_document_versions WHERE tenant_id = @tenant AND kind = 'PrivacyNotice' AND version = 2",
            ("tenant", Guid.Parse(rig.Tenant))));
    }

    // Review Focus 4: un formulario sin empresa (publicado antes de D-8) no puede resolver sus tokens: el mismo 404 de una configuración
    // incompleta. El nombre del negocio siempre se resuelve.
    [Fact]
    public async Task WithoutOrganizationItsVariablesAre404()
    {
        var factory = await host.StartTestAsync();
        var withCompanyToken = NewTenantId();
        var withTenantToken = NewTenantId();
        var slug = await SeedPublishedTenantAsync(factory, withCompanyToken, withOrganization: false);
        var otherSlug = await SeedPublishedTenantAsync(factory, withTenantToken, withOrganization: false);
        await PublishLegalDocumentAsync(factory, withCompanyToken, SupportLegalDocumentKind.Terms, 2, "<p>Términos de {{empresa}}.</p>");
        await PublishLegalDocumentAsync(
            factory, withTenantToken, SupportLegalDocumentKind.PrivacyNotice, 2, "<p>Aviso de {{nombre_comercial}}.</p>",
            "¿Autorizas a {{nombre_comercial}}?");
        using var client = VisitorClient(factory);

        var missing = await client.GetAsync(PublicUrl(slug, "/form"), Ct);
        var served = await client.GetAsync(PublicUrl(otherSlug, "/form"), Ct);

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("support.public.not_found", await CodeOfAsync(missing));
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        var body = await JsonAsync(served);
        var privacy = body.GetProperty("legal").GetProperty("privacyNotice");
        Assert.Equal(JsonValueKind.Null, body.GetProperty("organization").ValueKind);
        Assert.Equal($"<p>Aviso de {TenantName}.</p>", privacy.GetProperty("contentHtml").GetString());
        Assert.Equal($"¿Autorizas a {TenantName}?", privacy.GetProperty("authorizationQuestion").GetString());
    }

    // Un tenant con la versión 2 de los tres documentos con variables, sobre la empresa «Verde & Cía S.A.S.» con NIT 900123456.
    private async Task<Rig> RigAsync()
    {
        var factory = await host.StartTestAsync();
        var tenant = NewTenantId();
        var slug = await SeedPublishedTenantAsync(factory, tenant, organizationName: Company);
        await PublishLegalDocumentAsync(factory, tenant, SupportLegalDocumentKind.PrivacyNotice, 2, PrivacyTemplate, Question);
        await PublishLegalDocumentAsync(factory, tenant, SupportLegalDocumentKind.AnonymousNotice, 2, "<p>Aviso anónimo de {{empresa}}</p>");
        await PublishLegalDocumentAsync(
            factory, tenant, SupportLegalDocumentKind.Terms, 2, "<p>Términos de {{nombre_comercial}}, {{tipo_documento}} {{documento_identificacion}}.</p>");
        return new Rig(factory, tenant, slug, await FirstTypeIdAsync(factory, tenant), await AnyCityIdAsync(factory), VisitorClient(factory));
    }

    private static Task<string?> AcceptedAsync(Rig rig, string kind, string column) =>
        TextAsync(
            rig.Factory,
            $"SELECT {column} FROM support.legal_acceptances WHERE tenant_id = @tenant AND kind = @kind",
            ("tenant", Guid.Parse(rig.Tenant)), ("kind", kind));

    private sealed record Rig(QepApiFactory Factory, string Tenant, string Slug, Guid TypeId, Guid CityId, HttpClient Client);
}
