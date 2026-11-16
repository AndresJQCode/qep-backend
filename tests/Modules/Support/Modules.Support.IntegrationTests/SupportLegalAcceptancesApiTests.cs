using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Support.Application;
using Modules.Support.Domain;
using static Modules.Support.IntegrationTests.SupportApiHarness;
using static Modules.Support.IntegrationTests.SupportMessagingSeed;
using static Modules.Support.IntegrationTests.SupportPublicHarness;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// The legal acceptances in the console (spec 2026-11-15 §7, plan L19 and L20): the ticket detail lists them without content, and the
/// snapshot route returns what the requester was shown. Tickets are filed through the public portal (fake Turnstile); each test seeds
/// its own tenant and comes from its own IP.
/// </summary>
public sealed class SupportLegalAcceptancesApiTests(SupportPublicHost host) : IClassFixture<SupportPublicHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheTicketDetailListsTheAcceptances()
    {
        var visitorIp = NextIp();
        var named = await RigAsync(visitorIp);
        var namedTicket = await FileAsync(named, NamedBody(named.TypeId, named.CityId, Email()));
        var anonymous = await RigAsync();
        var anonymousTicket = await FileAsync(anonymous, AnonymousBody(anonymous.TypeId));

        var namedAcceptances = (await DetailAsync(named, namedTicket)).GetProperty("legalAcceptances").EnumerateArray().ToList();
        var anonymousAcceptances =
            (await DetailAsync(anonymous, anonymousTicket)).GetProperty("legalAcceptances").EnumerateArray().ToList();

        Assert.Equal(["PrivacyNotice", "Terms"], namedAcceptances.Select(item => item.GetProperty("kind").GetString()));
        Assert.All(namedAcceptances, item =>
        {
            Assert.Equal(1, item.GetProperty("version").GetInt32());
            Assert.Equal(visitorIp, item.GetProperty("visitorIp").GetString());
            Assert.NotEqual(default, item.GetProperty("acceptedAt").GetDateTimeOffset());
            // The detail carries no content: it opens with the snapshot route.
            Assert.False(item.TryGetProperty("contentHtml", out _));
        });
        Assert.Equal(["AnonymousNotice", "Terms"], anonymousAcceptances.Select(item => item.GetProperty("kind").GetString()));
        Assert.All(anonymousAcceptances, item => Assert.Equal(JsonValueKind.Null, item.GetProperty("visitorIp").ValueKind));
    }

    [Fact]
    public async Task AManualTicketHasNoAcceptances()
    {
        var rig = await RigAsync();
        var subject = Guid.CreateVersion7().ToString();
        await SeedCallerMembershipAsync(rig.Factory, "Ana Agente", "admin", rig.Tenant, subject);
        using var agent = CreateClient(rig.Factory, subject, rig.Tenant, SupportPermissions.Read, SupportPermissions.Manage);
        var created = await agent.PostAsJsonAsync($"{SupportUrl(rig.Tenant)}/tickets", SupportTicketScenario.Body(rig.TypeId), Ct);
        Assert.True(created.StatusCode == HttpStatusCode.Created, await BodyAsync(created));

        var acceptances = (await JsonAsync(created)).GetProperty("legalAcceptances");

        Assert.Equal(JsonValueKind.Array, acceptances.ValueKind);
        Assert.Empty(acceptances.EnumerateArray());
    }

    // Review Focus 4: the snapshot is the text that was served, even after the tenant publishes another version.
    [Fact]
    public async Task TheSnapshotIsTheServedText()
    {
        var visitorIp = NextIp();
        var rig = await RigAsync(visitorIp);
        var ticket = await FileAsync(rig, NamedBody(rig.TypeId, rig.CityId, Email()));
        foreach (var kind in SupportLegalDocumentKinds.All)
        {
            await PublishLegalDocumentAsync(
                rig.Factory, rig.Tenant, kind, 2, $"<p>{kind}, versión 2.</p>",
                kind == SupportLegalDocumentKind.PrivacyNotice ? "¿Otra pregunta?" : null,
                kind == SupportLegalDocumentKind.PrivacyNotice ? "https://example.com/otra" : null);
        }

        using var reader = CreateReader(rig.Factory, rig.Tenant);
        var privacy = await reader.GetAsync(SnapshotUrl(rig, ticket, "privacy-notice"), Ct);
        var terms = await reader.GetAsync(SnapshotUrl(rig, ticket, "terms"), Ct);

        Assert.Equal(HttpStatusCode.OK, privacy.StatusCode);
        var privacyBody = await JsonAsync(privacy);
        Assert.Equal("PrivacyNotice", privacyBody.GetProperty("kind").GetString());
        Assert.Equal(1, privacyBody.GetProperty("version").GetInt32());
        Assert.Equal("<p>PrivacyNotice, versión 1.</p>", privacyBody.GetProperty("contentHtml").GetString());
        Assert.Equal(
            "¿Autorizas a Support API Tests para el tratamiento de los datos personales suministrados en este formulario?",
            privacyBody.GetProperty("authorizationQuestion").GetString());
        Assert.Equal(JsonValueKind.Null, privacyBody.GetProperty("policyUrl").ValueKind);
        Assert.Equal(SupportLegalNotices.Characterization, privacyBody.GetProperty("characterizationText").GetString());
        Assert.Equal(visitorIp, privacyBody.GetProperty("visitorIp").GetString());

        Assert.Equal(HttpStatusCode.OK, terms.StatusCode);
        var termsBody = await terms.Content.ReadFromJsonAsync<SupportLegalAcceptanceSnapshotDto>(Ct);
        Assert.NotNull(termsBody);
        Assert.Equal("Terms", termsBody.Kind);
        Assert.Equal(1, termsBody.Version);
        Assert.Equal("<p>Terms, versión 1.</p>", termsBody.ContentHtml);
        Assert.Equal(visitorIp, termsBody.VisitorIp);
        Assert.Null(termsBody.AuthorizationQuestion);
        Assert.Null(termsBody.PolicyUrl);
        Assert.Null(termsBody.CharacterizationText);
    }

    // Final review S5: the snapshot is sanitized again on the way out. A snapshot edited by hand reaches the console clean, and the
    // stored evidence is not touched.
    [Fact]
    public async Task TheSnapshotIsResanitizedOnRead()
    {
        var rig = await RigAsync();
        var ticket = await FileAsync(rig, NamedBody(rig.TypeId, rig.CityId, Email()));
        const string handWritten = "<p>a</p><script>alert(1)</script>";
        await ExecuteAsync(
            rig.Factory,
            "UPDATE support.legal_acceptances SET content_html = @html WHERE ticket_id = @ticket AND kind = 'Terms'",
            ("html", handWritten),
            ("ticket", ticket));
        using var reader = CreateReader(rig.Factory, rig.Tenant);

        var body = await JsonAsync(await reader.GetAsync(SnapshotUrl(rig, ticket, "terms"), Ct));

        Assert.Equal("<p>a</p>", body.GetProperty("contentHtml").GetString());
        Assert.Equal(handWritten, await TextAsync(
            rig.Factory,
            "SELECT content_html FROM support.legal_acceptances WHERE ticket_id = @ticket AND kind = 'Terms'",
            ("ticket", ticket)));
    }

    [Fact]
    public async Task TheSnapshotOfAMissingKindIs404()
    {
        var rig = await RigAsync();
        var ticket = await FileAsync(rig, NamedBody(rig.TypeId, rig.CityId, Email()));
        using var reader = CreateReader(rig.Factory, rig.Tenant);

        var response = await reader.GetAsync(SnapshotUrl(rig, ticket, "anonymous-notice"), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("support.legal_acceptance.not_found", await CodeOfAsync(response));
    }

    [Fact]
    public async Task AnUnknownKindIs404()
    {
        var rig = await RigAsync();
        var ticket = await FileAsync(rig, NamedBody(rig.TypeId, rig.CityId, Email()));
        using var reader = CreateReader(rig.Factory, rig.Tenant);

        var response = await reader.GetAsync(SnapshotUrl(rig, ticket, "privacy_notice"), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("support.legal_documents.kind_not_found", await CodeOfAsync(response));
    }

    [Fact]
    public async Task TheSnapshotIsOnlyForTheTicketsTenant()
    {
        var mine = await RigAsync();
        var theirs = await RigAsync();
        var theirTicket = await FileAsync(theirs, NamedBody(theirs.TypeId, theirs.CityId, Email()));
        using var reader = CreateReader(mine.Factory, mine.Tenant);

        // Another tenant in the route: the caller is not a member there.
        var foreignRoute = await reader.GetAsync(SnapshotUrl(theirs, theirTicket, "terms"), Ct);
        // The caller's own tenant in the route, with a ticket of another tenant: it does not exist here.
        var foreignTicket = await reader.GetAsync(SnapshotUrl(mine, theirTicket, "terms"), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, foreignRoute.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignTicket.StatusCode);
        Assert.Equal("support.ticket.not_found", await CodeOfAsync(foreignTicket));
    }

    [Fact]
    public async Task AReaderCanReadTheSnapshot()
    {
        var rig = await RigAsync();
        var ticket = await FileAsync(rig, AnonymousBody(rig.TypeId));
        using var reader = CreateReader(rig.Factory, rig.Tenant);

        var response = await reader.GetAsync(SnapshotUrl(rig, ticket, "anonymous-notice"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("AnonymousNotice", body.GetProperty("kind").GetString());
        Assert.Equal("<p>AnonymousNotice, versión 1.</p>", body.GetProperty("contentHtml").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("visitorIp").ValueKind);
    }

    private async Task<Rig> RigAsync(string? visitorIp = null)
    {
        var factory = await host.StartTestAsync();
        var tenant = NewTenantId();
        var slug = await SeedPublishedTenantAsync(factory, tenant);
        return new Rig(
            factory,
            tenant,
            slug,
            await FirstTypeIdAsync(factory, tenant),
            await AnyCityIdAsync(factory),
            VisitorClient(factory, visitorIp));
    }

    private static async Task<Guid> FileAsync(Rig rig, object body)
    {
        var response = await PostJsonAsync(rig.Client, PublicUrl(rig.Slug, "/tickets"), body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await BodyAsync(response));
        // The ticket just filed: the latest number of the tenant.
        return Guid.Parse((await TextAsync(
            rig.Factory,
            "SELECT id::text FROM support.tickets WHERE tenant_id = @tenant ORDER BY number DESC LIMIT 1",
            ("tenant", Guid.Parse(rig.Tenant))))!);
    }

    private static async Task<JsonElement> DetailAsync(Rig rig, Guid ticketId)
    {
        using var reader = CreateReader(rig.Factory, rig.Tenant);
        var response = await reader.GetAsync($"{SupportUrl(rig.Tenant)}/tickets/{ticketId}", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    private static string SnapshotUrl(Rig rig, Guid ticketId, string kind) =>
        $"{SupportUrl(rig.Tenant)}/tickets/{ticketId}/legal-acceptances/{kind}";

    private static string Email() => $"ana-{Guid.CreateVersion7():N}@example.com";

    private sealed record Rig(QepApiFactory Factory, string Tenant, string Slug, Guid TypeId, Guid CityId, HttpClient Client);
}
