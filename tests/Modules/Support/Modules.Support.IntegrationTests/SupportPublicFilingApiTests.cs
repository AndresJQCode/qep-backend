using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Modules.Support.Application;
using Modules.Tenancy.Domain;
using static Modules.Support.IntegrationTests.SupportApiHarness;
using static Modules.Support.IntegrationTests.SupportMessagingSeed;
using static Modules.Support.IntegrationTests.SupportPublicHarness;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// Radicar desde el formulario público (plan fase 3, Task 6; F17, F21 a F23, Review Focus 5). Host propio por clase: Turnstile de
/// mentira, el correo de apertura que anota y todo el log en Trace capturado. Cada prueba siembra su tenant, llega desde su IP y mira
/// sólo los correos a su dirección.
/// </summary>
public sealed class SupportPublicFilingApiTests(SupportPublicLoggedHost host) : IClassFixture<SupportPublicLoggedHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FilingReturnsTheNumberAndASixDigitPinOnceAndStoresOnlyTheHash()
    {
        var rig = await RigAsync();

        var response = await PostJsonAsync(rig.Client, rig.TicketsUrl, NamedBody(rig.TypeId, rig.CityId, Email()));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("SOP-000001", body.GetProperty("number").GetString());
        var pin = body.GetProperty("pin").GetString()!;
        Assert.Matches("^[0-9]{6}$", pin);
        Assert.Equal("Recibimos tu caso.", body.GetProperty("successMessage").GetString());
        Assert.Equal(
            $"https://soporte.lulocrm.com/{rig.Slug}/consultar?radicado=SOP-000001", body.GetProperty("consultUrl").GetString());
        var hash = await TextAsync(
            rig.Factory,
            "SELECT a.pin_hash FROM support.ticket_access a JOIN support.tickets t ON t.id = a.ticket_id WHERE t.tenant_id = @tenant",
            ("tenant", rig.TenantId));
        Assert.NotNull(hash);
        Assert.NotEqual(pin, hash);
        Assert.DoesNotContain(pin, hash, StringComparison.Ordinal);
        Assert.True(rig.Factory.Services.GetRequiredService<ISupportPinHasher>().Verify(hash, pin));
        // La configuración del formulario no lo vuelve a mostrar: el PIN viaja una sola vez.
        Assert.DoesNotContain(pin, await BodyAsync(await rig.Client.GetAsync(PublicUrl(rig.Slug, "/form"), Ct)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANamedTicketEmailsThePinToTheRequester()
    {
        var rig = await RigAsync();
        var email = Email();

        var body = await JsonAsync(await PostJsonAsync(rig.Client, rig.TicketsUrl, NamedBody(rig.TypeId, rig.CityId, email)));

        Assert.True(body.GetProperty("pinEmailed").GetBoolean());
        var sent = Assert.Single(host.Emails.To(email));
        Assert.Equal(body.GetProperty("pin").GetString(), sent.Pin);
        Assert.Equal("SOP-000001", sent.TicketNumber);
        Assert.Equal("Support API Tests", sent.TenantName);
        Assert.Equal(body.GetProperty("consultUrl").GetString(), sent.ConsultUrl);
        Assert.DoesNotContain(sent.Pin, sent.ConsultUrl, StringComparison.Ordinal);
        Assert.Equal("Email", await AccessColumnAsync(rig, "last_sent_channel"));
        Assert.Null(await AccessColumnAsync(rig, "last_failure_code"));
    }

    [Fact]
    public async Task AFailedEmailStillFilesTheTicket()
    {
        var rig = await RigAsync();
        var email = Email();
        host.Emails.FailFor[email] = true;

        var response = await PostJsonAsync(rig.Client, rig.TicketsUrl, NamedBody(rig.TypeId, rig.CityId, email));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.False(body.GetProperty("pinEmailed").GetBoolean());
        Assert.Matches("^[0-9]{6}$", body.GetProperty("pin").GetString());
        Assert.Equal(1L, await CountAsync(rig, "SELECT count(*) FROM support.tickets WHERE tenant_id = @tenant"));
        Assert.Equal(SupportChannelReasons.SendFailed, await AccessColumnAsync(rig, "last_failure_code"));
        Assert.Equal("Email", await AccessColumnAsync(rig, "last_sent_channel"));
    }

    // Revisión del Bloque B (Minor 2, plan F23): si anotar la entrega falla después de confirmar el ticket (acá, la fila de la clave
    // desaparece antes del segundo guardado), el radicado ya existe y el PIN igual se muestra: 201 con el PIN, nunca un 500.
    [Fact]
    public async Task AFailedDeliveryBookkeepingStillReturnsThePin()
    {
        var rig = await RigAsync();
        var email = Email();
        host.Emails.BeforeSend[email] = () => ExecuteAsync(
            rig.Factory, "DELETE FROM support.ticket_access WHERE tenant_id = @tenant", ("tenant", rig.TenantId));

        var response = await PostJsonAsync(rig.Client, rig.TicketsUrl, NamedBody(rig.TypeId, rig.CityId, email));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Matches("^[0-9]{6}$", body.GetProperty("pin").GetString());
        Assert.Equal("SOP-000001", body.GetProperty("number").GetString());
        Assert.Equal(1L, await CountAsync(rig, "SELECT count(*) FROM support.tickets WHERE tenant_id = @tenant"));
    }

    // Review Focus 5: un anónimo no deja cliente, foto del solicitante, IP ni consentimiento. Se responde por la página.
    [Fact]
    public async Task AnAnonymousTicketStoresNoRequesterNoIpAndNoConsent()
    {
        var rig = await RigAsync();
        var sentBefore = host.Emails.Sent.Count;

        var response = await PostJsonAsync(rig.Client, rig.TicketsUrl, AnonymousBody(rig.TypeId));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.False(body.GetProperty("pinEmailed").GetBoolean());
        Assert.Equal(1L, await CountAsync(
            rig,
            """
            SELECT count(*) FROM support.tickets WHERE tenant_id = @tenant AND is_anonymous AND customer_id IS NULL
                AND reply_channel = 'Portal' AND requester_first_names IS NULL AND requester_email IS NULL
                AND requester_document_number IS NULL AND requester_mobile_phone IS NULL AND requester_country_code IS NULL
                AND requester_city_id IS NULL AND requester_city_name IS NULL
            """));
        Assert.Equal(0L, await CountAsync(rig, "SELECT count(*) FROM support.consents WHERE tenant_id = @tenant"));
        Assert.Equal(1L, await CountAsync(rig, "SELECT count(*) FROM support.ticket_access WHERE tenant_id = @tenant"));
        Assert.Equal(sentBefore, host.Emails.Sent.Count);
    }

    // Review Focus 5: la fila guarda el texto que la persona aceptó, aunque el tenant publique otro después.
    [Fact]
    public async Task TheConsentKeepsTheTextTheRequesterAccepted()
    {
        var visitorIp = NextIp();
        var rig = await RigAsync(visitorIp);
        Assert.Equal(
            HttpStatusCode.Created,
            (await PostJsonAsync(rig.Client, rig.TicketsUrl, NamedBody(rig.TypeId, rig.CityId, Email()))).StatusCode);

        await PublishLegalTextsAsync(rig.Factory, rig.Tenant, 2, "Autorizo, versión 2.");

        Assert.Equal(
            "Autorizo el tratamiento de mis datos personales, versión 1.",
            await TextAsync(rig.Factory, "SELECT authorization_text FROM support.consents WHERE tenant_id = @tenant", ("tenant", rig.TenantId)));
        Assert.Equal(1L, await CountAsync(rig, "SELECT count(*) FROM support.consents WHERE tenant_id = @tenant AND legal_text_version = 1"));
        Assert.Equal(
            visitorIp,
            await TextAsync(rig.Factory, "SELECT visitor_ip FROM support.consents WHERE tenant_id = @tenant", ("tenant", rig.TenantId)));
    }

    [Fact]
    public async Task AnOutdatedLegalTextVersionIs422()
    {
        var rig = await RigAsync();
        await PublishLegalTextsAsync(rig.Factory, rig.Tenant, 2, "Autorizo, versión 2.");

        var response = await PostJsonAsync(rig.Client, rig.TicketsUrl, NamedBody(rig.TypeId, rig.CityId, Email(), legalTextVersion: 1));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("support.public.legal_texts_outdated", await CodeOfAsync(response));
        Assert.Equal(0L, await CountAsync(rig, "SELECT count(*) FROM support.tickets WHERE tenant_id = @tenant"));
    }

    [Fact]
    public async Task AnOutdatedFormVersionIs422()
    {
        var rig = await RigAsync();

        var response = await PostJsonAsync(rig.Client, rig.TicketsUrl, NamedBody(rig.TypeId, rig.CityId, Email(), formVersion: 0));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("support.public.form_outdated", await CodeOfAsync(response));
        Assert.Equal(0L, await CountAsync(rig, "SELECT count(*) FROM support.tickets WHERE tenant_id = @tenant"));
    }

    [Fact]
    public async Task ABadCaptchaIs422AndFilesNothing()
    {
        var rig = await RigAsync();
        var email = Email();

        var response = await PostJsonAsync(rig.Client, rig.TicketsUrl, NamedBody(rig.TypeId, rig.CityId, email, token: "bot"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("support.public.captcha_invalid", await CodeOfAsync(response));
        Assert.Equal(0L, await CountAsync(rig, "SELECT count(*) FROM support.tickets WHERE tenant_id = @tenant"));
        Assert.Empty(host.Emails.To(email));
    }

    [Fact]
    public async Task AnUnpublishedOrInactiveTypeIs422()
    {
        var rig = await RigAsync();
        var inactive = Guid.Parse((await TextAsync(
            rig.Factory,
            "SELECT id::text FROM support.ticket_types WHERE tenant_id = @tenant AND id <> @first ORDER BY position LIMIT 1",
            ("tenant", rig.TenantId), ("first", rig.TypeId)))!);
        await ExecuteAsync(rig.Factory, "UPDATE support.ticket_types SET is_published = false WHERE id = @id", ("id", rig.TypeId));
        await ExecuteAsync(rig.Factory, "UPDATE support.ticket_types SET is_active = false WHERE id = @id", ("id", inactive));

        var unpublished = await PostJsonAsync(rig.Client, rig.TicketsUrl, NamedBody(rig.TypeId, rig.CityId, Email()));
        var deactivated = await PostJsonAsync(rig.Client, rig.TicketsUrl, NamedBody(inactive, rig.CityId, Email()));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, unpublished.StatusCode);
        Assert.Equal("support.ticket_type.unavailable", await CodeOfAsync(unpublished));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, deactivated.StatusCode);
        Assert.Equal("support.ticket_type.unavailable", await CodeOfAsync(deactivated));
        Assert.Equal(0L, await CountAsync(rig, "SELECT count(*) FROM support.tickets WHERE tenant_id = @tenant"));
    }

    // Plan F21: un solo cliente con ese documento se vincula; con dos, el ticket queda sin cliente y el detalle los ofrece.
    [Fact]
    public async Task OneMatchingCustomerIsLinkedAndTwoAreLeftAsCandidates()
    {
        var single = await RigAsync();
        var linked = await SeedIdentifiedCustomerAsync(single.Factory, single.TenantId, "Verde S.A.S.", "Nit", "900.123.456");
        var twice = await RigAsync();
        var first = await SeedIdentifiedCustomerAsync(twice.Factory, twice.TenantId, "Verde Uno", "Nit", "900.123.456");
        var second = await SeedIdentifiedCustomerAsync(twice.Factory, twice.TenantId, "Verde Dos", "Nit", "900123456");

        var one = await PostJsonAsync(
            single.Client, single.TicketsUrl, NamedBody(single.TypeId, single.CityId, Email(), documentType: "NIT", documentNumber: "900123456"));
        var two = await PostJsonAsync(
            twice.Client, twice.TicketsUrl, NamedBody(twice.TypeId, twice.CityId, Email(), documentType: "NIT", documentNumber: "900123456"));

        Assert.Equal(HttpStatusCode.Created, one.StatusCode);
        Assert.Equal(HttpStatusCode.Created, two.StatusCode);
        Assert.Equal(
            linked.ToString(),
            await TextAsync(single.Factory, "SELECT customer_id::text FROM support.tickets WHERE tenant_id = @tenant", ("tenant", single.TenantId)));
        var detail = await DetailAsync(twice);
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("customer").ValueKind);
        var candidates = detail.GetProperty("customerCandidates").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(2, candidates.Count);
        Assert.Contains(first, candidates);
        Assert.Contains(second, candidates);
        Assert.False(detail.GetProperty("isAnonymous").GetBoolean());
        Assert.Equal("Email", detail.GetProperty("consultAccess").GetProperty("channel").GetString());
    }

    [Fact]
    public async Task WithCustomersOffNoLookupHappens()
    {
        var rig = await RigAsync(modules: [TenantModuleKeys.Support]);
        await SeedIdentifiedCustomerAsync(rig.Factory, rig.TenantId, "Verde S.A.S.", "Nit", "900123456");

        var response = await PostJsonAsync(
            rig.Client, rig.TicketsUrl, NamedBody(rig.TypeId, rig.CityId, Email(), documentType: "NIT", documentNumber: "900123456"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1L, await CountAsync(rig, "SELECT count(*) FROM support.tickets WHERE tenant_id = @tenant AND customer_id IS NULL"));
    }

    [Fact]
    public async Task TheTicketIsPublicFormWithoutActorAndAuditedAsSystem()
    {
        var rig = await RigAsync();

        await PostJsonAsync(rig.Client, rig.TicketsUrl, NamedBody(rig.TypeId, rig.CityId, Email()));

        var ticketId = await TicketIdAsync(rig);
        Assert.Equal(1L, await CountAsync(
            rig,
            """
            SELECT count(*) FROM support.tickets WHERE tenant_id = @tenant AND intake = 'PublicForm' AND reply_channel = 'Email'
                AND assignee_member_id IS NULL AND NOT is_anonymous AND form_version_id IS NOT NULL
            """));
        Assert.Equal(1L, await CountAsync(
            rig, "SELECT count(*) FROM support.ticket_events WHERE tenant_id = @tenant AND kind = 'Created' AND actor_member_id IS NULL"));
        Assert.Equal(1L, await ScalarAsync(
            rig.Factory,
            "SELECT count(*) FROM audit.entries WHERE action = @action AND resource_id = @id AND actor_type = 'System'",
            ("action", SupportAuditActions.TicketCreated), ("id", ticketId.ToString())));
    }

    // Plan Global Constraints: el PIN no llega a ningún log (capturado en Trace), ni a la auditoría ni a la traza del ticket.
    [Fact]
    public async Task ThePinNeverReachesLogsAuditOrEvents()
    {
        var rig = await RigAsync();
        host.Logs.Entries.Clear();

        var body = await JsonAsync(await PostJsonAsync(rig.Client, rig.TicketsUrl, NamedBody(rig.TypeId, rig.CityId, Email())));
        var pin = body.GetProperty("pin").GetString()!;
        var ticketId = await TicketIdAsync(rig);

        // Con límites de dígito y hexadecimal: un GUID o una duración podrían contener seis dígitos seguidos por casualidad.
        var standalone = new Regex($"(?<![0-9A-Fa-f]){pin}(?![0-9A-Fa-f])", RegexOptions.None, TimeSpan.FromSeconds(1));
        Assert.NotEmpty(host.Logs.Entries);
        Assert.DoesNotContain(host.Logs.Entries, standalone.IsMatch);
        Assert.Equal(0L, await ScalarAsync(
            rig.Factory,
            "SELECT count(*) FROM audit.entries WHERE resource_id = @id AND (changed_fields::text LIKE @pin OR action LIKE @pin)",
            ("id", ticketId.ToString()), ("pin", $"%{pin}%")));
        Assert.Equal(0L, await CountAsync(
            rig, $"SELECT count(*) FROM support.ticket_events WHERE tenant_id = @tenant AND details::text LIKE '%{pin}%'"));
    }

    private async Task<Rig> RigAsync(string? visitorIp = null, IEnumerable<TenantModuleKey>? modules = null)
    {
        var factory = await host.StartTestAsync();
        var tenant = NewTenantId();
        var slug = await SeedPublishedTenantAsync(factory, tenant, modules);
        return new Rig(
            factory,
            tenant,
            Guid.Parse(tenant),
            slug,
            await FirstTypeIdAsync(factory, tenant),
            await AnyCityIdAsync(factory),
            VisitorClient(factory, visitorIp));
    }

    private static string Email() => $"ana-{Guid.CreateVersion7():N}@example.com";

    private static Task<long> CountAsync(Rig rig, string sql) => ScalarAsync(rig.Factory, sql, ("tenant", rig.TenantId));

    private static Task<string?> AccessColumnAsync(Rig rig, string column) =>
        TextAsync(rig.Factory, $"SELECT {column} FROM support.ticket_access WHERE tenant_id = @tenant", ("tenant", rig.TenantId));

    private static async Task<Guid> TicketIdAsync(Rig rig) =>
        Guid.Parse((await TextAsync(
            rig.Factory, "SELECT id::text FROM support.tickets WHERE tenant_id = @tenant LIMIT 1", ("tenant", rig.TenantId)))!);

    private static async Task<JsonElement> DetailAsync(Rig rig)
    {
        using var console = CreateClient(rig.Factory, Guid.CreateVersion7().ToString(), rig.Tenant, SupportPermissions.Read);
        var response = await console.GetAsync($"{SupportUrl(rig.Tenant)}/tickets/{await TicketIdAsync(rig)}", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    private sealed record Rig(
        QepApiFactory Factory, string Tenant, Guid TenantId, string Slug, Guid TypeId, Guid CityId, HttpClient Client)
    {
        public string TicketsUrl => PublicUrl(Slug, "/tickets");
    }
}
