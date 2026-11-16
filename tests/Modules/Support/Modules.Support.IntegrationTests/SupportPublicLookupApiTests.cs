using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Support.Application;
using Modules.Support.Domain;
using static Modules.Support.IntegrationTests.SupportApiHarness;
using static Modules.Support.IntegrationTests.SupportMessagingSeed;
using static Modules.Support.IntegrationTests.SupportPublicHarness;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// Consultar un ticket con radicado y PIN, y reenviar el PIN (plan fase 3, Task 7; F7, F9, F10, F13; Review Focus 1, 2 y 4). Host propio
/// por clase con el reloj del tenant movible, como <see cref="ClockedSupportHost"/>: el bloqueo de 15 minutos y el enfriamiento de 10 se
/// prueban adelantándolo. Cada prueba siembra su tenant y llega desde sus propias IPs (los limitadores son por IP).
/// </summary>
public sealed class SupportPublicLookupApiTests(SupportPublicHost host) : IClassFixture<SupportPublicHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheRightPinShowsStatusDueAndOnlySentReplies()
    {
        var rig = await RigAsync();
        var filed = await FileNamedAsync(rig);
        var ticketId = await TicketIdAsync(rig, filed.Number);
        await SeedReplyAsync(rig, ticketId, "Ya revisamos tu caso.", SupportReplyStatus.Sent);
        await SeedReplyAsync(rig, ticketId, "Esta no salió.", SupportReplyStatus.Failed);
        await SeedReplyAsync(rig, ticketId, "Esta sigue en curso.", SupportReplyStatus.Pending);

        var response = await LookupAsync(rig, filed.Number, filed.Pin);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = await JsonAsync(response);
        Assert.Equal(filed.Number, body.GetProperty("number").GetString());
        Assert.Equal("New", body.GetProperty("status").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("typeName").GetString()));
        Assert.Equal(ClockedSupportHost.Reception, body.GetProperty("receivedAt").GetDateTimeOffset());
        Assert.True(body.TryGetProperty("dueOn", out _));
        Assert.True(body.TryGetProperty("dueState", out _));
        Assert.True(body.TryGetProperty("termIsLegal", out _));
        var reply = Assert.Single(body.GetProperty("replies").EnumerateArray());
        Assert.Equal("Ya revisamos tu caso.", reply.GetProperty("text").GetString());
        Assert.Equal("Email", reply.GetProperty("channel").GetString());
        // Nada más del expediente (plan F13): ni el solicitante, ni el asunto, ni el PIN.
        var raw = await BodyAsync(response);
        Assert.DoesNotContain("Gómez", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(filed.Pin, raw, StringComparison.Ordinal);
    }

    // Spec 2026-11-15 §5.4 and Review Focus 5: an internal note never reaches the requester's consult page.
    [Fact]
    public async Task TheConsultNeverShowsANote()
    {
        var rig = await RigAsync();
        var filed = await FileNamedAsync(rig);
        var ticketId = await TicketIdAsync(rig, filed.Number);
        await SeedReplyAsync(rig, ticketId, "Ya revisamos tu caso.", SupportReplyStatus.Sent);
        await using (var scope = rig.Factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ISupportTicketNoteRepository>().Add(SupportTicketNote.Create(
                Guid.CreateVersion7(), rig.TenantId, new SupportTicketId(ticketId), Guid.CreateVersion7(),
                "Nota interna: el cliente es difícil.", [], ClockedSupportHost.Reception.AddHours(2)));
            await scope.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
        }

        var response = await LookupAsync(rig, filed.Number, filed.Pin);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await BodyAsync(response);
        Assert.Contains("Ya revisamos tu caso.", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Nota interna", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("difícil", raw, StringComparison.Ordinal);
    }

    // Review Focus 1: radicado inexistente, PIN errado y radicado bloqueado responden igual: status, código y cuerpo.
    [Fact]
    public async Task AnUnknownNumberAWrongPinAndALockedTicketAnswerTheSame()
    {
        var rig = await RigAsync();
        var filed = await FileNamedAsync(rig);
        var locked = await FileNamedAsync(rig, NextIp());
        for (var i = 0; i < SupportTicketAccess.MaxFailures; i++)
        {
            await LookupAsync(rig, locked.Number, WrongPin(locked.Pin), NextIp());
        }

        var responses = new[]
        {
            await LookupAsync(rig, "SOP-999999", filed.Pin, NextIp()),
            await LookupAsync(rig, filed.Number, WrongPin(filed.Pin), NextIp()),
            await LookupAsync(rig, locked.Number, locked.Pin, NextIp()),
            await LookupAsync(rig, "no-es-un-radicado", "abc", NextIp()),
        };

        var bodies = new List<string>();
        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("support.public.lookup_failed", await CodeOfAsync(response));
            bodies.Add(WithoutTraceId(await JsonAsync(response)));
        }

        Assert.Single(bodies.Distinct(StringComparer.Ordinal));
    }

    // Review Focus 2: cinco fallos seguidos bloquean el radicado 15 minutos, también para el PIN correcto; después vuelve a abrir.
    [Fact]
    public async Task FiveWrongPinsLockTheTicketEvenForTheRightPin()
    {
        var rig = await RigAsync();
        var filed = await FileNamedAsync(rig);
        for (var i = 0; i < SupportTicketAccess.MaxFailures; i++)
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await LookupAsync(rig, filed.Number, WrongPin(filed.Pin), NextIp())).StatusCode);
        }

        var whileLocked = await LookupAsync(rig, filed.Number, filed.Pin, NextIp());
        host.Clock.UtcNow = ClockedSupportHost.Reception + SupportTicketAccess.LockFor - TimeSpan.FromSeconds(1);
        var almost = await LookupAsync(rig, filed.Number, filed.Pin, NextIp());
        host.Clock.UtcNow = ClockedSupportHost.Reception + SupportTicketAccess.LockFor + TimeSpan.FromMinutes(1);
        var reopened = await LookupAsync(rig, filed.Number, filed.Pin, NextIp());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, whileLocked.StatusCode);
        Assert.Equal("support.public.lookup_failed", await CodeOfAsync(whileLocked));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, almost.StatusCode);
        Assert.Equal(HttpStatusCode.OK, reopened.StatusCode);
        Assert.Equal(0L, await ScalarAsync(
            rig.Factory, "SELECT failed_attempts FROM support.ticket_access WHERE tenant_id = @tenant", ("tenant", rig.TenantId)));
    }

    // Review Focus 2: el bloqueo es por radicado; otro ticket del mismo tenant sigue consultable. Y un fallo no toca la versión del
    // ticket (vive en ticket_access), así que no le da un 412 al asesor.
    [Fact]
    public async Task ALockedTicketDoesNotLockAnother()
    {
        var rig = await RigAsync();
        var locked = await FileNamedAsync(rig);
        var other = await FileNamedAsync(rig, NextIp());
        var versionBefore = await VersionAsync(rig, locked.Number);
        for (var i = 0; i < SupportTicketAccess.MaxFailures; i++)
        {
            await LookupAsync(rig, locked.Number, WrongPin(locked.Pin), NextIp());
        }

        var response = await LookupAsync(rig, other.Number, other.Pin, NextIp());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(other.Number, (await JsonAsync(response)).GetProperty("number").GetString());
        Assert.Equal(versionBefore, await VersionAsync(rig, locked.Number));
    }

    // Revisión del Bloque B (Important 1): veinte PIN errados a la vez, cada uno desde su IP, no se pisan el contador: la fila de la
    // clave se lee con FOR UPDATE y el radicado queda bloqueado, también para el PIN correcto.
    [Fact]
    public async Task ConcurrentWrongPinsStillLockTheTicket()
    {
        var rig = await RigAsync();
        var filed = await FileNamedAsync(rig);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => LookupAsync(rig, filed.Number, WrongPin(filed.Pin), NextIp())));
        var right = await LookupAsync(rig, filed.Number, filed.Pin, NextIp());

        Assert.All(attempts, response => Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, right.StatusCode);
        Assert.Equal(1L, await ScalarAsync(
            rig.Factory,
            "SELECT count(*) FROM support.ticket_access WHERE tenant_id = @tenant AND locked_until > @now",
            ("tenant", rig.TenantId), ("now", ClockedSupportHost.Reception)));
    }

    // Revisión del Bloque B (Minor 4): cinco reenvíos a la vez pasado el enfriamiento mandan un solo correo; los demás encuentran el
    // intento ya anotado.
    [Fact]
    public async Task ConcurrentResendsSendOnce()
    {
        var rig = await RigAsync();
        var email = Email();
        var filed = await FileNamedAsync(rig, email: email);
        host.Clock.UtcNow = ClockedSupportHost.Reception + SupportTicketAccess.ResendCooldown + TimeSpan.FromMinutes(1);

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => ResendAsync(rig, filed.Number)));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Accepted, response.StatusCode));
        var emails = host.Emails.To(email);
        Assert.Equal(2, emails.Count);
        Assert.Equal(HttpStatusCode.OK, (await LookupAsync(rig, filed.Number, emails[^1].Pin, NextIp())).StatusCode);
    }

    // Triage #8 (Bloque F): el reclamo del reenvío se confirma antes de mandar, y lo que confirma es el enfriamiento y un envío en curso
    // (send_pending), no un envío exitoso. Si el proceso muere entre los dos guardados, la consola ve que no salió; el segundo guardado
    // escribe el resultado real.
    [Fact]
    public async Task WhileTheResendIsInFlightTheAccessReadsSendPending()
    {
        var rig = await RigAsync();
        var email = Email();
        var filed = await FileNamedAsync(rig, email: email);
        string? channelInFlight = null;
        string? failureInFlight = null;
        host.Emails.BeforeSend[email] = async () =>
        {
            channelInFlight = await AccessColumnAsync(rig, "last_sent_channel");
            failureInFlight = await AccessColumnAsync(rig, "last_failure_code");
        };
        host.Clock.UtcNow = ClockedSupportHost.Reception + SupportTicketAccess.ResendCooldown + TimeSpan.FromMinutes(1);

        var response = await ResendAsync(rig, filed.Number);
        host.Emails.BeforeSend.TryRemove(email, out _);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("Email", channelInFlight);
        Assert.Equal(SupportChannelReasons.SendPending, failureInFlight);
        Assert.Equal("Email", await AccessColumnAsync(rig, "last_sent_channel"));
        Assert.Null(await AccessColumnAsync(rig, "last_failure_code"));
    }

    [Fact]
    public async Task TheNumberAcceptsWithAndWithoutPrefix()
    {
        var rig = await RigAsync();
        var filed = await FileNamedAsync(rig);

        var withPrefix = await LookupAsync(rig, filed.Number, filed.Pin);
        var lowercase = await LookupAsync(rig, filed.Number.ToLowerInvariant(), filed.Pin);
        var bare = await LookupAsync(rig, "1", filed.Pin);

        Assert.Equal(HttpStatusCode.OK, withPrefix.StatusCode);
        Assert.Equal(HttpStatusCode.OK, lowercase.StatusCode);
        Assert.Equal(HttpStatusCode.OK, bare.StatusCode);
        Assert.Equal("SOP-000001", (await JsonAsync(bare)).GetProperty("number").GetString());
    }

    [Fact]
    public async Task ATicketOfAnotherTenantIsNotFoundUnderThisSlug()
    {
        var rig = await RigAsync();
        var filed = await FileNamedAsync(rig);
        var other = await RigAsync();

        var response = await PostJsonAsync(
            other.Client, PublicUrl(other.Slug, "/tickets/lookup"),
            new { number = filed.Number, pin = filed.Pin, turnstileToken = FakeSupportCaptcha.ValidToken });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("support.public.lookup_failed", await CodeOfAsync(response));
    }

    // Review Focus 4: inexistente, anónimo, en enfriamiento y válido responden el mismo 202 vacío. Sólo el válido manda un correo, al
    // correo de la foto aunque el cuerpo traiga otro; el PIN viejo deja de servir y el nuevo sí sirve.
    [Fact]
    public async Task ResendPinAnswersTheSameAndOnlyWritesToTheRegisteredEmail()
    {
        var rig = await RigAsync();
        var email = Email();
        var filed = await FileNamedAsync(rig, email: email);
        var anonymous = await FileAnonymousAsync(rig);
        Assert.Single(host.Emails.To(email));

        var unknown = await ResendAsync(rig, "SOP-999999");
        var toAnonymous = await ResendAsync(rig, anonymous);
        var cooling = await ResendAsync(rig, filed.Number);
        host.Clock.UtcNow = ClockedSupportHost.Reception + SupportTicketAccess.ResendCooldown + TimeSpan.FromMinutes(1);
        var valid = await ResendAsync(rig, filed.Number);

        foreach (var response in new[] { unknown, toAnonymous, cooling, valid })
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            Assert.Equal(string.Empty, await BodyAsync(response));
        }

        Assert.Empty(host.Emails.To("intruso@example.com"));
        var emails = host.Emails.To(email);
        Assert.Equal(2, emails.Count);
        var newPin = emails[^1].Pin;
        Assert.NotEqual(filed.Pin, newPin);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await LookupAsync(rig, filed.Number, filed.Pin, NextIp())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LookupAsync(rig, filed.Number, newPin, NextIp())).StatusCode);
    }

    [Fact]
    public async Task ResendPinWithinTenMinutesSendsNothing()
    {
        var rig = await RigAsync();
        var email = Email();
        var filed = await FileNamedAsync(rig, email: email);
        host.Clock.UtcNow = ClockedSupportHost.Reception + SupportTicketAccess.ResendCooldown - TimeSpan.FromSeconds(1);

        var response = await ResendAsync(rig, filed.Number);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Single(host.Emails.To(email));
        Assert.Equal(HttpStatusCode.OK, (await LookupAsync(rig, filed.Number, filed.Pin, NextIp())).StatusCode);
    }

    // Owner, 2026-11-15 (E1): consultar y reenviar el PIN no dependen del formulario publicado. Un tenant que lo despublica sigue
    // atendiendo a quien ya radicó; lo que se cierra es el formulario y radicar.
    [Fact]
    public async Task LookupAndResendWorkWhileTheFormIsUnpublished()
    {
        var rig = await RigAsync();
        var email = Email();
        var filed = await FileNamedAsync(rig, email: email);
        await ExecuteAsync(
            rig.Factory, "UPDATE support.form_versions SET is_published = false WHERE tenant_id = @tenant", ("tenant", rig.TenantId));

        var lookup = await LookupAsync(rig, filed.Number, filed.Pin, NextIp());
        host.Clock.UtcNow = ClockedSupportHost.Reception + SupportTicketAccess.ResendCooldown + TimeSpan.FromMinutes(1);
        var resend = await ResendAsync(rig, filed.Number);

        Assert.True(lookup.StatusCode == HttpStatusCode.OK, await BodyAsync(lookup));
        Assert.Equal(filed.Number, (await JsonAsync(lookup)).GetProperty("number").GetString());
        Assert.Equal(HttpStatusCode.Accepted, resend.StatusCode);
        var emails = host.Emails.To(email);
        Assert.Equal(2, emails.Count);
        Assert.Equal(HttpStatusCode.OK, (await LookupAsync(rig, filed.Number, emails[^1].Pin, NextIp())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rig.Client.GetAsync(PublicUrl(rig.Slug, "/form"), Ct)).StatusCode);
        using var filer = VisitorClient(rig.Factory, NextIp());
        var filing = await PostJsonAsync(filer, PublicUrl(rig.Slug, "/tickets"), NamedBody(rig.TypeId, rig.CityId, Email()));
        Assert.Equal(HttpStatusCode.NotFound, filing.StatusCode);
        Assert.Equal("support.public.not_found", await CodeOfAsync(filing));
    }

    [Fact]
    public async Task ABadCaptchaOnLookupIs422CaptchaInvalid()
    {
        var rig = await RigAsync();
        var filed = await FileNamedAsync(rig);

        var response = await PostJsonAsync(
            rig.Client, PublicUrl(rig.Slug, "/tickets/lookup"), new { number = filed.Number, pin = filed.Pin, turnstileToken = "bot" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("support.public.captcha_invalid", await CodeOfAsync(response));
        // No cuenta como intento: el PIN correcto sigue sirviendo y el contador está en cero.
        Assert.Equal(0L, await ScalarAsync(
            rig.Factory, "SELECT failed_attempts FROM support.ticket_access WHERE tenant_id = @tenant", ("tenant", rig.TenantId)));
    }

    private async Task<Rig> RigAsync()
    {
        var factory = await host.StartTestAsync();
        var tenant = NewTenantId();
        var slug = await SeedPublishedTenantAsync(factory, tenant);
        return new Rig(
            factory, Guid.Parse(tenant), slug, await FirstTypeIdAsync(factory, tenant), await AnyCityIdAsync(factory), VisitorClient(factory));
    }

    // Cada radicado llega desde su propia IP: el cupo de radicar es de 5 cada 10 minutos.
    private static async Task<(string Number, string Pin)> FileNamedAsync(Rig rig, string? visitorIp = null, string? email = null)
    {
        using var client = VisitorClient(rig.Factory, visitorIp ?? NextIp());
        var response = await PostJsonAsync(client, PublicUrl(rig.Slug, "/tickets"), NamedBody(rig.TypeId, rig.CityId, email ?? Email()));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await BodyAsync(response));
        var body = await JsonAsync(response);
        return (body.GetProperty("number").GetString()!, body.GetProperty("pin").GetString()!);
    }

    private static async Task<string> FileAnonymousAsync(Rig rig)
    {
        using var client = VisitorClient(rig.Factory, NextIp());
        var response = await PostJsonAsync(client, PublicUrl(rig.Slug, "/tickets"), AnonymousBody(rig.TypeId));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await BodyAsync(response));
        return (await JsonAsync(response)).GetProperty("number").GetString()!;
    }

    private static async Task<HttpResponseMessage> LookupAsync(Rig rig, string number, string pin, string? visitorIp = null)
    {
        if (visitorIp is null)
        {
            return await PostJsonAsync(
                rig.Client, PublicUrl(rig.Slug, "/tickets/lookup"), new { number, pin, turnstileToken = FakeSupportCaptcha.ValidToken });
        }

        using var client = VisitorClient(rig.Factory, visitorIp);
        return await PostJsonAsync(
            client, PublicUrl(rig.Slug, "/tickets/lookup"), new { number, pin, turnstileToken = FakeSupportCaptcha.ValidToken });
    }

    // El cuerpo trae un correo a propósito: «Reenviar PIN» nunca usa un destino del visitante.
    private static async Task<HttpResponseMessage> ResendAsync(Rig rig, string number)
    {
        using var client = VisitorClient(rig.Factory, NextIp());
        return await PostJsonAsync(
            client,
            PublicUrl(rig.Slug, "/tickets/resend-pin"),
            new { number, email = "intruso@example.com", turnstileToken = FakeSupportCaptcha.ValidToken });
    }

    private static async Task SeedReplyAsync(Rig rig, Guid ticketId, string text, SupportReplyStatus status)
    {
        await using var scope = rig.Factory.Services.CreateAsyncScope();
        var reply = SupportTicketReply.Start(
            rig.TenantId, new SupportTicketId(ticketId), Guid.CreateVersion7(), SupportReplyChannel.Email, SupportReplyDelivery.Email, text,
            null, Guid.CreateVersion7(), ClockedSupportHost.Reception);
        if (status is SupportReplyStatus.Sent)
        {
            reply.MarkSent(null, ClockedSupportHost.Reception.AddHours(1));
        }
        else if (status is SupportReplyStatus.Failed)
        {
            reply.MarkFailed(SupportChannelReasons.SendFailed, null, ClockedSupportHost.Reception.AddHours(1));
        }

        scope.ServiceProvider.GetRequiredService<ISupportReplyRepository>().Add(reply);
        await scope.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
    }

    private static async Task<Guid> TicketIdAsync(Rig rig, string number) =>
        Guid.Parse((await TextAsync(
            rig.Factory,
            "SELECT id::text FROM support.tickets WHERE tenant_id = @tenant AND number = @number",
            ("tenant", rig.TenantId), ("number", NumberOf(number))))!);

    private static Task<string?> AccessColumnAsync(Rig rig, string column) =>
        TextAsync(rig.Factory, $"SELECT {column} FROM support.ticket_access WHERE tenant_id = @tenant", ("tenant", rig.TenantId));

    private static Task<long> VersionAsync(Rig rig, string number) =>
        ScalarAsync(
            rig.Factory,
            "SELECT version FROM support.tickets WHERE tenant_id = @tenant AND number = @number",
            ("tenant", rig.TenantId), ("number", NumberOf(number)));

    private static long NumberOf(string displayNumber) =>
        SupportTicketNumber.TryParse(displayNumber, out var number) ? number : throw new ArgumentException(displayNumber);

    // Un PIN de seis dígitos que no es el correcto.
    private static string WrongPin(string pin) => pin == "000000" ? "000001" : "000000";

    private static string Email() => $"ana-{Guid.CreateVersion7():N}@example.com";

    private static string WithoutTraceId(JsonElement body) =>
        JsonSerializer.Serialize(body.EnumerateObject()
            .Where(property => property.Name != "traceId")
            .ToDictionary(property => property.Name, property => property.Value.ToString()));

    private sealed record Rig(QepApiFactory Factory, Guid TenantId, string Slug, Guid TypeId, Guid CityId, HttpClient Client);
}
