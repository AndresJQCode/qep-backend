using System.Net;
using System.Text.Json;
using Modules.Support.Application;
using static Modules.Support.IntegrationTests.SupportApiHarness;
using static Modules.Support.IntegrationTests.SupportTicketScenario;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// Spec 2026-11-16: «Generar PIN nuevo» de un ticket anónimo desde la consola (<c>POST …/tickets/{id}/pin</c>). Host propio de la
/// clase (<see cref="ClockedSupportHost"/>): la consulta pública tiene un cupo de 10 por minuto por IP, y en este host todas las
/// consultas salen de la misma, así que la clase entera usa menos de diez. El reloj del tenant no se mueve: un bloqueo sigue vigente
/// durante toda la prueba.
/// </summary>
public sealed class SupportPinReissueApiTests(ClockedSupportHost host) : IClassFixture<ClockedSupportHost>
{
    private static Dictionary<string, object?> AnonymousBody(Guid typeId)
    {
        var body = Body(typeId, replyChannel: "Portal");
        body["requester"] = null;
        body["anonymous"] = true;
        return body;
    }

    private static async Task<JsonElement> CreateAnonymousAsync(SupportTicketScenario scenario) =>
        await scenario.CreateAsync(AnonymousBody(await scenario.TypeIdAsync("Queja")));

    private static Task<HttpResponseMessage> ReissueAsync(SupportTicketScenario scenario, JsonElement ticket, long? version) =>
        scenario.PostActionAsync($"tickets/{Id(ticket)}/pin", null, version);

    private static async Task<HttpResponseMessage> LookupAsync(SupportTicketScenario scenario, string number, string pin)
    {
        var slug = await SupportPublicHarness.SlugOfAsync(scenario.Factory, scenario.Tenant);
        // En Development el Turnstile está apagado: cualquier token pasa.
        using var visitor = scenario.Factory.CreateClient();
        return await SupportPublicHarness.PostJsonAsync(
            visitor, SupportPublicHarness.PublicUrl(slug, "/tickets/lookup"), new { number, pin, turnstileToken = "any" });
    }

    private static string WrongPin(string pin) => pin == "000000" ? "000001" : "000000";

    private static string Number(JsonElement ticket) => ticket.GetProperty("number").GetString()!;

    private static string Pin(JsonElement ticket) => ticket.GetProperty("issuedPin").GetString()!;

    [Fact]
    public async Task ReissuingReturnsTheNewPinAndConsultUrlOnlyOnce()
    {
        var scenario = await StartAsync(await host.StartTestAsync());
        var ticket = await CreateAnonymousAsync(scenario);
        var slug = await SupportPublicHarness.SlugOfAsync(scenario.Factory, scenario.Tenant);

        var response = await ReissueAsync(scenario, ticket, Version(ticket));

        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        var body = await JsonAsync(response);
        Assert.Matches(@"^\d{6}$", Pin(body));
        Assert.EndsWith($"/{slug}/consultar?radicado={Number(ticket)}", body.GetProperty("consultUrl").GetString());
        Assert.Equal(Version(ticket) + 1, Version(body));
        var detail = await scenario.DetailAsync(Id(ticket));
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("issuedPin").ValueKind);
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("consultUrl").ValueKind);
    }

    [Fact]
    public async Task TheOldPinStopsWorkingAndTheNewOneWorks()
    {
        var scenario = await StartAsync(await host.StartTestAsync());
        var ticket = await CreateAnonymousAsync(scenario);
        var reissued = await JsonAsync(await ReissueAsync(scenario, ticket, Version(ticket)));
        // Si los dos PIN coincidieran (uno en un millón) el viejo seguiría sirviendo; la prueba lo dice antes de consultar.
        Assert.NotEqual(Pin(ticket), Pin(reissued));

        var old = await LookupAsync(scenario, Number(ticket), Pin(ticket));
        var fresh = await LookupAsync(scenario, Number(ticket), Pin(reissued));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, old.StatusCode);
        Assert.Equal("support.public.lookup_failed", await CodeOfAsync(old));
        Assert.True(fresh.StatusCode == HttpStatusCode.OK, await BodyAsync(fresh));
    }

    // P-2: el asesor es la verificación humana; los fallos se hicieron contra un PIN que ya no existe.
    [Fact]
    public async Task ReissuingUnlocksALockedConsult()
    {
        var scenario = await StartAsync(await host.StartTestAsync());
        var ticket = await CreateAnonymousAsync(scenario);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.Equal(
                HttpStatusCode.UnprocessableEntity, (await LookupAsync(scenario, Number(ticket), WrongPin(Pin(ticket)))).StatusCode);
        }

        var reissued = await JsonAsync(await ReissueAsync(scenario, ticket, Version(ticket)));
        var lookup = await LookupAsync(scenario, Number(ticket), Pin(reissued));

        Assert.True(lookup.StatusCode == HttpStatusCode.OK, await BodyAsync(lookup));
        Assert.Equal(Number(ticket), (await JsonAsync(lookup)).GetProperty("number").GetString());
    }

    [Fact]
    public async Task ANamedTicketIs422WithoutEventNorAudit()
    {
        var scenario = await StartAsync(await host.StartTestAsync());
        var ticket = await scenario.CreateAsync();

        var response = await ReissueAsync(scenario, ticket, Version(ticket));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("support.ticket.pin_not_anonymous", await CodeOfAsync(response));
        var detail = await scenario.DetailAsync(Id(ticket));
        Assert.Equal(Version(ticket), Version(detail));
        Assert.DoesNotContain(
            detail.GetProperty("events").EnumerateArray(), item => item.GetProperty("kind").GetString() == "PinReissued");
        Assert.Equal(0, await CountAuditEntriesAsync(scenario.Factory, SupportAuditActions.TicketPinReissued, Id(ticket).ToString()));
    }

    [Fact]
    public async Task TheTraceRecordsPinReissuedWithItsActorAndTheAuditNeverHasThePin()
    {
        var scenario = await StartAsync(await host.StartTestAsync());
        var ticket = await CreateAnonymousAsync(scenario);
        var before = host.Publisher.Of(scenario.TenantId).Count;

        var body = await JsonAsync(await ReissueAsync(scenario, ticket, Version(ticket)));

        var reissued = body.GetProperty("events").EnumerateArray().Last();
        Assert.Equal("PinReissued", reissued.GetProperty("kind").GetString());
        Assert.Equal(scenario.CallerMemberId, reissued.GetProperty("actor").GetProperty("id").GetGuid());
        Assert.Equal(1, await CountAuditEntriesAsync(scenario.Factory, SupportAuditActions.TicketPinReissued, Id(ticket).ToString()));
        Assert.Equal(1, host.Publisher.Of(scenario.TenantId).Count - before);
        var pin = Pin(body);
        Assert.Equal(0L, await SupportMessagingSeed.ScalarAsync(
            scenario.Factory,
            "SELECT count(*) FROM audit.entries WHERE resource_id = @id AND changed_fields::text LIKE '%' || @pin || '%'",
            ("id", Id(ticket).ToString()),
            ("pin", pin)));
        Assert.Equal(0L, await SupportMessagingSeed.ScalarAsync(
            scenario.Factory,
            "SELECT count(*) FROM support.ticket_events WHERE ticket_id = @id AND details::text LIKE '%' || @pin || '%'",
            ("id", Id(ticket)),
            ("pin", pin)));
    }

    // P-3: es acceso para leer, no edición del caso.
    [Fact]
    public async Task AClosedAnonymousTicketCanBeReissued()
    {
        var scenario = await StartAsync(await host.StartTestAsync());
        var ticket = await CreateAnonymousAsync(scenario);
        ticket = await scenario.MoveAsync(ticket, "Resolved");
        ticket = await scenario.MoveAsync(ticket, "Closed");

        var response = await ReissueAsync(scenario, ticket, Version(ticket));

        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        Assert.Equal("Closed", (await JsonAsync(response)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task WithoutIfMatchIs428AndWithAnOldVersionIs412()
    {
        var scenario = await StartAsync(await host.StartTestAsync());
        var ticket = await CreateAnonymousAsync(scenario);
        var first = await JsonAsync(await ReissueAsync(scenario, ticket, Version(ticket)));

        var withoutVersion = await ReissueAsync(scenario, ticket, version: null);
        // El segundo clic con la versión vieja: no genera otro PIN sin que la pantalla se entere.
        var stale = await ReissueAsync(scenario, ticket, Version(ticket));

        Assert.Equal(HttpStatusCode.PreconditionRequired, withoutVersion.StatusCode);
        Assert.Equal("precondition.if_match_required", await CodeOfAsync(withoutVersion));
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal("concurrency.conflict", await CodeOfAsync(stale));
        Assert.Equal(Version(first), Version(await scenario.DetailAsync(Id(ticket))));
        Assert.Equal(1, await CountAuditEntriesAsync(scenario.Factory, SupportAuditActions.TicketPinReissued, Id(ticket).ToString()));
    }

    [Fact]
    public async Task ATicketOfAnotherTenantUnderMyRouteIs404()
    {
        var scenario = await StartAsync(await host.StartTestAsync());
        var other = await StartAsync(scenario.Factory);
        var foreign = await CreateAnonymousAsync(other);

        var response = await ReissueAsync(scenario, foreign, Version(foreign));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("support.ticket.not_found", await CodeOfAsync(response));
        Assert.Equal(Version(foreign), Version(await other.DetailAsync(Id(foreign))));
    }

    [Fact]
    public async Task AReaderCannotReissue()
    {
        var scenario = await StartAsync(await host.StartTestAsync());
        var ticket = await CreateAnonymousAsync(scenario);
        using var reader = CreateClient(scenario.Factory, scenario.SubjectId, scenario.Tenant, SupportPermissions.Read);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{scenario.Url}/tickets/{Id(ticket)}/pin");
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{Version(ticket)}\"");

        var response = await reader.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(Version(ticket), Version(await scenario.DetailAsync(Id(ticket))));
    }

    // P-6: el enlace viaja sólo con el PIN; el alta con nombre no trae ninguno de los dos.
    [Fact]
    public async Task TheAnonymousCreationReturnsTheConsultUrlAndTheNamedOneDoesNot()
    {
        var scenario = await StartAsync(await host.StartTestAsync());
        var slug = await SupportPublicHarness.SlugOfAsync(scenario.Factory, scenario.Tenant);

        var anonymous = await CreateAnonymousAsync(scenario);
        var named = await scenario.CreateAsync();

        Assert.EndsWith($"/{slug}/consultar?radicado={Number(anonymous)}", anonymous.GetProperty("consultUrl").GetString());
        Assert.Equal(JsonValueKind.Null, named.GetProperty("consultUrl").ValueKind);
        Assert.Equal(JsonValueKind.Null, named.GetProperty("issuedPin").ValueKind);
        Assert.Equal(JsonValueKind.Null, (await scenario.DetailAsync(Id(anonymous))).GetProperty("consultUrl").ValueKind);
    }
}
