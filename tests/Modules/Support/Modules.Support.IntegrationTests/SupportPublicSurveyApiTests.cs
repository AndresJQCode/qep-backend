using System.Net;
using System.Text.Json;
using Modules.Support.Application;
using Modules.Support.Domain;
using static Modules.Support.IntegrationTests.SupportApiHarness;
using static Modules.Support.IntegrationTests.SupportMessagingSeed;
using static Modules.Support.IntegrationTests.SupportPublicHarness;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// Fase 4b, Tarea 13 (H19 a H21, Review Focus 5): calificar por el enlace del correo (el token en el cuerpo, sin Turnstile) y por la
/// página de consulta (radicado y PIN, con la misma verificación que la consulta). Host propio por clase con el log en <c>Trace</c>
/// capturado: la prueba de que el token no llega a ningún log lo necesita. La encuesta se deja mandada por SQL (el envío lo amarran
/// <c>SupportSurveyApiTests</c>), con un token conocido por la prueba. Cada prueba siembra su tenant y llega desde sus propias IPs.
/// </summary>
public sealed class SupportPublicSurveyApiTests(SupportPublicLoggedHost host) : IClassFixture<SupportPublicLoggedHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheLinkShowsTheSurveyAndAnswersItOnce()
    {
        var rig = await RigAsync();
        var filed = await FileNamedAsync(rig);
        var token = await ResolveWithEmailSurveyAsync(rig, filed.Number);

        var shown = await PostAsync(rig, "/surveys/lookup", new { token });
        var answered = await PostAsync(rig, "/surveys/answer", new { token, rating = "Good", comment = "  Muy bien atendido.  " });
        var again = await PostAsync(rig, "/surveys/answer", new { token, rating = "Bad" });

        Assert.Equal(HttpStatusCode.OK, shown.StatusCode);
        Assert.Equal("no-store", shown.Headers.CacheControl?.ToString());
        var before = await JsonAsync(shown);
        Assert.Equal(filed.Number, before.GetProperty("number").GetString());
        Assert.False(string.IsNullOrEmpty(before.GetProperty("tenantName").GetString()));
        Assert.False(string.IsNullOrEmpty(before.GetProperty("typeName").GetString()));
        Assert.True(before.GetProperty("canRate").GetBoolean());
        Assert.Equal(JsonValueKind.Null, before.GetProperty("rating").ValueKind);
        Assert.Equal(["Good", "Regular", "Bad"], before.GetProperty("options").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
        var after = await JsonAsync(answered);
        Assert.False(after.GetProperty("canRate").GetBoolean());
        Assert.Equal("Good", after.GetProperty("rating").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
        Assert.Equal("support.survey.already_answered", await CodeOfAsync(again));
        var ticketId = await TicketIdAsync(rig, filed.Number);
        Assert.Equal(
            "Good|Email|Muy bien atendido.",
            await TextAsync(
                rig.Factory,
                "SELECT rating || '|' || answered_via || '|' || comment FROM support.ticket_surveys WHERE ticket_id = @id",
                ("id", ticketId)));
        Assert.Equal(1, await CountAuditEntriesAsync(rig.Factory, SupportAuditActions.TicketSurveyAnswered, ticketId.ToString()));
    }

    // H19: un token que no existe y uno mal formado responden lo mismo: 404 support.survey.not_found, con el mismo cuerpo.
    [Fact]
    public async Task AnUnknownOrMalformedTokenIs404WithTheSameBody()
    {
        var rig = await RigAsync();

        var unknown = await PostAsync(rig, "/surveys/lookup", new { token = SupportSurveyTokens.NewToken() });
        var malformed = await PostAsync(rig, "/surveys/lookup", new { token = "no-es-un-token" });
        var missing = await PostAsync(rig, "/surveys/answer", new { rating = "Good" });

        var bodies = new List<string>();
        foreach (var response in new[] { unknown, malformed, missing })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("support.survey.not_found", await CodeOfAsync(response));
            bodies.Add(WithoutTraceId(await JsonAsync(response)));
        }

        Assert.Single(bodies.Distinct(StringComparer.Ordinal));
    }

    // H19: la encuesta se busca filtrada por el tenant del slug: el token de otro tenant es un 404, no la encuesta de otro.
    [Fact]
    public async Task ATokenOfAnotherTenantIs404()
    {
        var rig = await RigAsync();
        var other = await RigAsync();
        var filed = await FileNamedAsync(rig);
        var token = await ResolveWithEmailSurveyAsync(rig, filed.Number);

        var response = await PostAsync(other, "/surveys/lookup", new { token });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("support.survey.not_found", await CodeOfAsync(response));
    }

    // H20: un ticket anónimo sólo califica por la página de consulta, con su radicado y su PIN.
    [Fact]
    public async Task TheConsultRatesAnAnonymousTicket()
    {
        var rig = await RigAsync();
        var filed = await FileAnonymousAsync(rig);
        await ResolveWithPortalSurveyAsync(rig, filed.Number);

        var response = await RateAsync(rig, filed.Number, filed.Pin, "Regular", "Podría ser más rápido.");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var survey = (await JsonAsync(response)).GetProperty("survey");
        Assert.False(survey.GetProperty("canRate").GetBoolean());
        Assert.Equal("Regular", survey.GetProperty("rating").GetString());
        Assert.Equal(
            "Regular|Portal|Podría ser más rápido.",
            await TextAsync(
                rig.Factory,
                "SELECT rating || '|' || answered_via || '|' || comment FROM support.ticket_surveys WHERE ticket_id = @id",
                ("id", await TicketIdAsync(rig, filed.Number))));
    }

    // H20: calificar por la consulta verifica igual que consultar. Un PIN errado cuenta como un intento de consulta; al quinto el
    // radicado queda bloqueado, también para el PIN correcto, y nada se califica.
    [Fact]
    public async Task AWrongPinOnRatingCountsLikeALookup()
    {
        var rig = await RigAsync();
        var filed = await FileNamedAsync(rig);
        await ResolveWithPortalSurveyAsync(rig, filed.Number);
        var wrong = filed.Pin == "000000" ? "000001" : "000000";

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var failed = await RateAsync(rig, filed.Number, wrong, "Good", null);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, failed.StatusCode);
            Assert.Equal("support.public.lookup_failed", await CodeOfAsync(failed));
        }

        var locked = await RateAsync(rig, filed.Number, filed.Pin, "Good", null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, locked.StatusCode);
        Assert.Equal("support.public.lookup_failed", await CodeOfAsync(locked));
        Assert.Equal(
            "5", await TextAsync(rig.Factory, "SELECT failed_attempts::text FROM support.ticket_access WHERE tenant_id = @tenant", ("tenant", rig.TenantId)));
        Assert.Equal(
            "", await TextAsync(rig.Factory, "SELECT coalesce(rating, '') FROM support.ticket_surveys WHERE tenant_id = @tenant", ("tenant", rig.TenantId)));
        // Revisión del bloque D: el bloqueo que se alcanzó por la calificación es el mismo de la consulta.
        var lookup = await LookupAsync(rig, filed.Number, filed.Pin);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, lookup.StatusCode);
        Assert.Equal("support.public.lookup_failed", await CodeOfAsync(lookup));
    }

    // H21 (Q-10): reabierto, esa resolución ya no se juzga: con el PIN correcto, 422 support.survey.closed.
    [Fact]
    public async Task ARatingOnAReopenedTicketIs422Closed()
    {
        var rig = await RigAsync();
        var filed = await FileNamedAsync(rig);
        var ticketId = await ResolveWithPortalSurveyAsync(rig, filed.Number);
        await SupportTicketSeed.UpdateTicketAsync(
            rig.Factory, rig.TenantId, new SupportTicketId(ticketId),
            ticket => ticket.ChangeStatus(SupportTicketStatus.Open, null, DateTimeOffset.UtcNow, SupportTicketSeed.Clock));

        var response = await RateAsync(rig, filed.Number, filed.Pin, "Bad", null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("support.survey.closed", await CodeOfAsync(response));
    }

    // H20: la consulta dice si se puede calificar. Sin resolver no hay encuesta; resuelta y sin procesar, todavía no se califica; ya
    // mandada, sí.
    [Fact]
    public async Task TheLookupSaysWhetherItCanBeRated()
    {
        var rig = await RigAsync();
        var filed = await FileNamedAsync(rig);

        var open = await JsonAsync(await LookupAsync(rig, filed.Number, filed.Pin));
        var ticketId = await TicketIdAsync(rig, filed.Number);
        await SupportTicketSeed.UpdateTicketAsync(
            rig.Factory, rig.TenantId, new SupportTicketId(ticketId),
            ticket => ticket.ChangeStatus(SupportTicketStatus.Resolved, null, DateTimeOffset.UtcNow, SupportTicketSeed.Clock));
        var pending = await JsonAsync(await LookupAsync(rig, filed.Number, filed.Pin));
        await MarkSurveySentAsync(rig, ticketId, "Portal", tokenHash: null);
        var sent = await JsonAsync(await LookupAsync(rig, filed.Number, filed.Pin));

        Assert.Equal(JsonValueKind.Null, open.GetProperty("survey").ValueKind);
        Assert.False(pending.GetProperty("survey").GetProperty("canRate").GetBoolean());
        Assert.True(sent.GetProperty("survey").GetProperty("canRate").GetBoolean());
        Assert.Equal(JsonValueKind.Null, sent.GetProperty("survey").GetProperty("rating").ValueKind);
        Assert.Equal(["Good", "Regular", "Bad"], sent.GetProperty("survey").GetProperty("options").EnumerateArray().Select(item => item.GetString()));
    }

    // Review Focus 5: el token viaja en el cuerpo y nunca llega a un log (host en Trace), a la auditoría, a la traza ni a un evento.
    [Fact]
    public async Task TheSurveyTokenNeverReachesLogsAuditOrEvents()
    {
        var rig = await RigAsync();
        var filed = await FileNamedAsync(rig);
        var token = await ResolveWithEmailSurveyAsync(rig, filed.Number);
        host.Logs.Entries.Clear();

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(rig, "/surveys/lookup", new { token })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(rig, "/surveys/answer", new { token, rating = "Good" })).StatusCode);

        Assert.NotEmpty(host.Logs.Entries);
        Assert.DoesNotContain(host.Logs.Entries, entry => entry.Contains(token, StringComparison.Ordinal));
        var like = $"%{token}%";
        Assert.Equal(0L, await ScalarAsync(
            rig.Factory,
            "SELECT count(*) FROM audit.entries WHERE tenant_id = @tenant AND (changed_fields::text LIKE @like OR resource_id LIKE @like)",
            ("tenant", rig.TenantId), ("like", like)));
        Assert.Equal(0L, await ScalarAsync(
            rig.Factory, "SELECT count(*) FROM support.ticket_events WHERE tenant_id = @tenant AND details::text LIKE @like",
            ("tenant", rig.TenantId), ("like", like)));
        Assert.Equal(0L, await ScalarAsync(
            rig.Factory, "SELECT count(*) FROM platform.outbox_messages WHERE payload::text LIKE @like", ("like", like)));
    }

    private async Task<Rig> RigAsync()
    {
        var factory = await host.StartTestAsync();
        var tenant = NewTenantId();
        var slug = await SeedPublishedTenantAsync(factory, tenant);
        return new Rig(factory, Guid.Parse(tenant), slug, await FirstTypeIdAsync(factory, tenant), await AnyCityIdAsync(factory));
    }

    private static async Task<(string Number, string Pin)> FileNamedAsync(Rig rig)
    {
        using var client = VisitorClient(rig.Factory, NextIp());
        var response = await PostJsonAsync(
            client, PublicUrl(rig.Slug, "/tickets"), NamedBody(rig.TypeId, rig.CityId, $"ana-{Guid.CreateVersion7():N}@example.com"));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await BodyAsync(response));
        var body = await JsonAsync(response);
        return (body.GetProperty("number").GetString()!, body.GetProperty("pin").GetString()!);
    }

    private static async Task<(string Number, string Pin)> FileAnonymousAsync(Rig rig)
    {
        using var client = VisitorClient(rig.Factory, NextIp());
        var response = await PostJsonAsync(client, PublicUrl(rig.Slug, "/tickets"), AnonymousBody(rig.TypeId));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await BodyAsync(response));
        var body = await JsonAsync(response);
        return (body.GetProperty("number").GetString()!, body.GetProperty("pin").GetString()!);
    }

    // Resuelto, con la encuesta mandada por correo con un token que la prueba conoce (sólo su hash queda en la fila, H19).
    private static async Task<string> ResolveWithEmailSurveyAsync(Rig rig, string number)
    {
        var ticketId = await ResolveAsync(rig, number);
        var token = SupportSurveyTokens.NewToken();
        await MarkSurveySentAsync(rig, ticketId, "Email", SupportSurveyTokens.HashOf(token));
        return token;
    }

    private static async Task<Guid> ResolveWithPortalSurveyAsync(Rig rig, string number)
    {
        var ticketId = await ResolveAsync(rig, number);
        await MarkSurveySentAsync(rig, ticketId, "Portal", tokenHash: null);
        return ticketId;
    }

    private static async Task<Guid> ResolveAsync(Rig rig, string number)
    {
        var ticketId = await TicketIdAsync(rig, number);
        await SupportTicketSeed.UpdateTicketAsync(
            rig.Factory, rig.TenantId, new SupportTicketId(ticketId),
            ticket => ticket.ChangeStatus(SupportTicketStatus.Resolved, null, DateTimeOffset.UtcNow, SupportTicketSeed.Clock));
        return ticketId;
    }

    private static Task MarkSurveySentAsync(Rig rig, Guid ticketId, string channel, byte[]? tokenHash) =>
        ExecuteAsync(
            rig.Factory,
            """
            UPDATE support.ticket_surveys SET status = 'Sent', channel = @channel, token_hash = @hash, sent_at = now()
            WHERE ticket_id = @id
            """,
            ("channel", channel), ("hash", tokenHash), ("id", ticketId));

    private static async Task<HttpResponseMessage> PostAsync(Rig rig, string path, object body)
    {
        using var client = VisitorClient(rig.Factory, NextIp());
        return await PostJsonAsync(client, PublicUrl(rig.Slug, path), body);
    }

    private static Task<HttpResponseMessage> RateAsync(Rig rig, string number, string pin, string rating, string? comment) =>
        PostAsync(rig, "/tickets/rating", new { number, pin, turnstileToken = FakeSupportCaptcha.ValidToken, rating, comment });

    private static Task<HttpResponseMessage> LookupAsync(Rig rig, string number, string pin) =>
        PostAsync(rig, "/tickets/lookup", new { number, pin, turnstileToken = FakeSupportCaptcha.ValidToken });

    private static async Task<Guid> TicketIdAsync(Rig rig, string number) =>
        Guid.Parse((await TextAsync(
            rig.Factory,
            "SELECT id::text FROM support.tickets WHERE tenant_id = @tenant AND number = @number",
            ("tenant", rig.TenantId),
            ("number", SupportTicketNumber.TryParse(number, out var value) ? value : throw new ArgumentException(number))))!);

    private static string WithoutTraceId(JsonElement body) =>
        JsonSerializer.Serialize(body.EnumerateObject()
            .Where(property => property.Name != "traceId")
            .ToDictionary(property => property.Name, property => property.Value.ToString()));

    private sealed record Rig(QepApiFactory Factory, Guid TenantId, string Slug, Guid TypeId, Guid CityId);
}
