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
/// The idempotent public filing (spec 2026-11-15 §9.2, plan L27 and L28; Review Focus 6 and 7). A retry with the same
/// <c>clientRequestId</c> in the same tenant answers 201 with the first ticket, before Turnstile, the validator and the version checks,
/// never creates a second ticket nor spends a number, and never sends the opening email again. Its PIN: null if the first one went out
/// by email, a new one (the old one stops working) if it went out by no channel. Own host per class (<see cref="SupportPublicHost"/>,
/// not <c>SharedSupportHost</c>: it needs the fake Turnstile and the recording opening email); each test seeds its tenant and every
/// POST comes from its own visitor IP, so the SupportPublicSubmit quota never interferes.
/// </summary>
public sealed class SupportPublicIdempotencyApiTests(SupportPublicHost host) : IClassFixture<SupportPublicHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ARetryWithTheSameClientRequestIdReturnsTheSameTicket()
    {
        var rig = await RigAsync();
        var email = Email();
        var key = Guid.NewGuid();

        var first = await FileAsync(rig, NamedBody(rig.TypeId, rig.CityId, email, clientRequestId: key));
        var retry = await FileAsync(rig, NamedBody(rig.TypeId, rig.CityId, email, clientRequestId: key));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        var one = await JsonAsync(first);
        var two = await JsonAsync(retry);
        Assert.Equal("SOP-000001", one.GetProperty("number").GetString());
        foreach (var field in new[] { "number", "receivedAt", "dueOn", "termBusinessDays", "termIsLegal", "successMessage", "consultUrl" })
        {
            Assert.Equal(one.GetProperty(field).ToString(), two.GetProperty(field).ToString());
        }

        Assert.Equal(1L, await CountAsync(rig, "SELECT count(*) FROM support.tickets WHERE tenant_id = @tenant"));
        Assert.Equal(1L, await ScalarAsync(
            rig.Factory,
            "SELECT count(*) FROM support.tickets WHERE tenant_id = @tenant AND client_request_id = @key",
            ("tenant", rig.TenantId),
            ("key", key)));
        Assert.Single(host.Emails.To(email));

        var next = await FileAsync(rig, NamedBody(rig.TypeId, rig.CityId, Email()));
        Assert.Equal(HttpStatusCode.Created, next.StatusCode);
        Assert.Equal("SOP-000002", (await JsonAsync(next)).GetProperty("number").GetString());
    }

    // Review Focus 7: the PIN already went out by email. The retry does not show it again nor send it again, and it still works.
    [Fact]
    public async Task ARetryAfterTheEmailWentOutReturnsNoPin()
    {
        var rig = await RigAsync();
        var email = Email();
        var key = Guid.NewGuid();
        var first = await JsonAsync(await FileAsync(rig, NamedBody(rig.TypeId, rig.CityId, email, clientRequestId: key)));
        Assert.True(first.GetProperty("pinEmailed").GetBoolean());

        var retry = await FileAsync(rig, NamedBody(rig.TypeId, rig.CityId, email, clientRequestId: key));

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        var body = await JsonAsync(retry);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("pin").ValueKind);
        Assert.True(body.GetProperty("pinEmailed").GetBoolean());
        Assert.Single(host.Emails.To(email));
        var pin = first.GetProperty("pin").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await LookupAsync(rig, first.GetProperty("number").GetString()!, pin)).StatusCode);
    }

    // Review Focus 7: an anonymous ticket never gets an email. The retry issues a new PIN, shown only in the response; the old one
    // stops working.
    [Fact]
    public async Task ARetryOfAnAnonymousTicketRotatesThePin()
    {
        var rig = await RigAsync();
        var key = Guid.NewGuid();
        var sentBefore = host.Emails.Sent.Count;
        var first = await JsonAsync(await FileAsync(rig, AnonymousBody(rig.TypeId, clientRequestId: key)));

        var retry = await FileAsync(rig, AnonymousBody(rig.TypeId, clientRequestId: key));

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        var body = await JsonAsync(retry);
        var number = first.GetProperty("number").GetString()!;
        Assert.Equal(number, body.GetProperty("number").GetString());
        var oldPin = first.GetProperty("pin").GetString()!;
        var newPin = body.GetProperty("pin").GetString()!;
        Assert.Matches("^[0-9]{6}$", newPin);
        Assert.False(body.GetProperty("pinEmailed").GetBoolean());
        var oldLookup = await LookupAsync(rig, number, oldPin);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, oldLookup.StatusCode);
        Assert.Equal("support.public.lookup_failed", await CodeOfAsync(oldLookup));
        Assert.Equal(HttpStatusCode.OK, (await LookupAsync(rig, number, newPin)).StatusCode);
        Assert.Equal(sentBefore, host.Emails.Sent.Count);
    }

    // The first email failed: the PIN went out by no channel. The retry issues a new one and does not try the email again.
    [Fact]
    public async Task ARetryAfterAFailedEmailRotatesThePin()
    {
        var rig = await RigAsync();
        var email = Email();
        var key = Guid.NewGuid();
        host.Emails.FailFor[email] = true;
        var first = await JsonAsync(await FileAsync(rig, NamedBody(rig.TypeId, rig.CityId, email, clientRequestId: key)));
        Assert.False(first.GetProperty("pinEmailed").GetBoolean());

        var retry = await FileAsync(rig, NamedBody(rig.TypeId, rig.CityId, email, clientRequestId: key));

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        var body = await JsonAsync(retry);
        var number = first.GetProperty("number").GetString()!;
        var newPin = body.GetProperty("pin").GetString()!;
        Assert.Matches("^[0-9]{6}$", newPin);
        Assert.False(body.GetProperty("pinEmailed").GetBoolean());
        Assert.Single(host.Emails.To(email));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await LookupAsync(rig, number, first.GetProperty("pin").GetString()!)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LookupAsync(rig, number, newPin)).StatusCode);
    }

    // C-2: the opening email of the first attempt is still unrecorded (simulated by clearing what the delivery wrote). Within the grace
    // it is in flight: the retry neither rotates nor shows the PIN, and the one being emailed keeps working.
    [Fact]
    public async Task ARetryWhileTheOpeningEmailIsInFlightKeepsThePin()
    {
        var rig = await RigAsync();
        var email = Email();
        var key = Guid.NewGuid();
        var first = await JsonAsync(await FileAsync(rig, NamedBody(rig.TypeId, rig.CityId, email, clientRequestId: key)));
        await ForgetDeliveryAsync(rig);
        host.Clock.UtcNow = ClockedSupportHost.Reception.AddMinutes(5);

        var retry = await FileAsync(rig, NamedBody(rig.TypeId, rig.CityId, email, clientRequestId: key));

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        var body = await JsonAsync(retry);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("pin").ValueKind);
        Assert.True(body.GetProperty("pinEmailed").GetBoolean());
        var number = first.GetProperty("number").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await LookupAsync(rig, number, first.GetProperty("pin").GetString()!)).StatusCode);
        Assert.Single(host.Emails.To(email));
    }

    // Final review S3: a delivery still unrecorded more than 10 minutes after the PIN is a failed one (the process died between commit
    // and recording it). The retry rotates, as after a failed email, and sends nothing.
    [Fact]
    public async Task ARetryPastTheGraceOfAnUnrecordedEmailRotatesThePin()
    {
        var rig = await RigAsync();
        var email = Email();
        var key = Guid.NewGuid();
        var first = await JsonAsync(await FileAsync(rig, NamedBody(rig.TypeId, rig.CityId, email, clientRequestId: key)));
        await ForgetDeliveryAsync(rig);
        host.Clock.UtcNow = ClockedSupportHost.Reception + FilePublicTicketHandler.OpeningDeliveryGrace + TimeSpan.FromMinutes(1);

        var retry = await FileAsync(rig, NamedBody(rig.TypeId, rig.CityId, email, clientRequestId: key));

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        var body = await JsonAsync(retry);
        var number = first.GetProperty("number").GetString()!;
        var newPin = body.GetProperty("pin").GetString()!;
        Assert.Matches("^[0-9]{6}$", newPin);
        Assert.False(body.GetProperty("pinEmailed").GetBoolean());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await LookupAsync(rig, number, first.GetProperty("pin").GetString()!)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LookupAsync(rig, number, newPin)).StatusCode);
        Assert.Single(host.Emails.To(email));
    }

    // The key is looked up before Turnstile (the token was spent by the first attempt) and before the form and legal version checks.
    [Fact]
    public async Task ARetrySkipsTurnstileAndTheVersionChecks()
    {
        var rig = await RigAsync();
        var key = Guid.NewGuid();
        var first = await JsonAsync(await FileAsync(rig, NamedBody(rig.TypeId, rig.CityId, Email(), clientRequestId: key)));
        await PublishFormVersionAsync(rig, 2);
        await PublishLegalDocumentAsync(rig.Factory, rig.Tenant, SupportLegalDocumentKind.Terms, 2, "<p>Términos, versión 2.</p>");

        var retry = await FileAsync(rig, NamedBody(rig.TypeId, rig.CityId, Email(), token: "bot", clientRequestId: key));

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(first.GetProperty("number").GetString(), (await JsonAsync(retry)).GetProperty("number").GetString());
        Assert.Equal(first.GetProperty("successMessage").GetString(), (await JsonAsync(retry)).GetProperty("successMessage").GetString());
        Assert.Equal(1L, await CountAsync(rig, "SELECT count(*) FROM support.tickets WHERE tenant_id = @tenant"));
    }

    // The body is not compared (like the store checkout): the first ticket wins as it was filed.
    [Fact]
    public async Task ARetryWithADifferentBodyStillReturnsTheFirstTicket()
    {
        var rig = await RigAsync();
        var key = Guid.NewGuid();
        var otherType = Guid.Parse((await TextAsync(
            rig.Factory,
            "SELECT id::text FROM support.ticket_types WHERE tenant_id = @tenant AND id <> @first ORDER BY position LIMIT 1",
            ("tenant", rig.TenantId),
            ("first", rig.TypeId)))!);
        var first = await JsonAsync(await FileAsync(rig, NamedBody(rig.TypeId, rig.CityId, Email(), clientRequestId: key)));

        var retry = await FileAsync(
            rig, NamedBody(otherType, rig.CityId, Email(), clientRequestId: key, subject: "Otro asunto distinto"));

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(first.GetProperty("number").GetString(), (await JsonAsync(retry)).GetProperty("number").GetString());
        Assert.Equal(1L, await CountAsync(rig, "SELECT count(*) FROM support.tickets WHERE tenant_id = @tenant"));
        Assert.Equal(1L, await ScalarAsync(
            rig.Factory,
            "SELECT count(*) FROM support.tickets WHERE tenant_id = @tenant AND subject = 'No me llegó el pedido' AND type_id = @type",
            ("tenant", rig.TenantId),
            ("type", rig.TypeId)));
    }

    // Review Focus 6: two simultaneous POSTs with the same key. The number counter serializes them and the second finds the key under
    // its lock: one ticket, one pair of legal acceptances, one email, and both answers carry the same number.
    [Fact]
    public async Task TwoConcurrentFilingsWithTheSameKeyCreateOneTicket()
    {
        var rig = await RigAsync();
        var email = Email();
        var key = Guid.NewGuid();

        var responses = await Task.WhenAll(
            FileAsync(rig, NamedBody(rig.TypeId, rig.CityId, email, clientRequestId: key)),
            FileAsync(rig, NamedBody(rig.TypeId, rig.CityId, email, clientRequestId: key)));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
        var numbers = new List<string>();
        foreach (var response in responses)
        {
            numbers.Add((await JsonAsync(response)).GetProperty("number").GetString()!);
        }

        Assert.Equal(["SOP-000001", "SOP-000001"], numbers);
        Assert.Equal(1L, await CountAsync(rig, "SELECT count(*) FROM support.tickets WHERE tenant_id = @tenant"));
        Assert.Equal(2L, await CountAsync(rig, "SELECT count(*) FROM support.legal_acceptances WHERE tenant_id = @tenant"));
        Assert.Single(host.Emails.To(email));
        var next = await FileAsync(rig, NamedBody(rig.TypeId, rig.CityId, Email()));
        Assert.Equal("SOP-000002", (await JsonAsync(next)).GetProperty("number").GetString());
    }

    // The lookup is always by tenant: the same key in another tenant is another ticket.
    [Fact]
    public async Task TheSameKeyInAnotherTenantIsAnotherTicket()
    {
        var rig = await RigAsync();
        var other = await RigAsync();
        var key = Guid.NewGuid();

        var here = await FileAsync(rig, NamedBody(rig.TypeId, rig.CityId, Email(), clientRequestId: key));
        var there = await FileAsync(other, NamedBody(other.TypeId, other.CityId, Email(), clientRequestId: key));

        Assert.Equal(HttpStatusCode.Created, here.StatusCode);
        Assert.Equal(HttpStatusCode.Created, there.StatusCode);
        Assert.Equal(1L, await CountAsync(rig, "SELECT count(*) FROM support.tickets WHERE tenant_id = @tenant"));
        Assert.Equal(1L, await CountAsync(other, "SELECT count(*) FROM support.tickets WHERE tenant_id = @tenant"));
        Assert.Equal(2L, await ScalarAsync(
            rig.Factory, "SELECT count(*) FROM support.tickets WHERE client_request_id = @key", ("key", key)));
    }

    [Fact]
    public async Task WithoutAKeyEveryFilingIsNew()
    {
        var rig = await RigAsync();
        var email = Email();

        var first = await FileAsync(rig, NamedBody(rig.TypeId, rig.CityId, email));
        var second = await FileAsync(rig, NamedBody(rig.TypeId, rig.CityId, email));

        Assert.Equal("SOP-000001", (await JsonAsync(first)).GetProperty("number").GetString());
        Assert.Equal("SOP-000002", (await JsonAsync(second)).GetProperty("number").GetString());
        Assert.Equal(2L, await CountAsync(rig, "SELECT count(*) FROM support.tickets WHERE tenant_id = @tenant AND client_request_id IS NULL"));
    }

    // Plan L27: the key is neither audited nor written to ticket_events.
    [Fact]
    public async Task TheKeyIsNeverInTheTraceNorTheAudit()
    {
        var rig = await RigAsync();
        var key = Guid.NewGuid();

        Assert.Equal(
            HttpStatusCode.Created,
            (await FileAsync(rig, NamedBody(rig.TypeId, rig.CityId, Email(), clientRequestId: key))).StatusCode);
        Assert.Equal(
            HttpStatusCode.Created,
            (await FileAsync(rig, NamedBody(rig.TypeId, rig.CityId, Email(), clientRequestId: key))).StatusCode);

        var ticketId = await TextAsync(
            rig.Factory, "SELECT id::text FROM support.tickets WHERE tenant_id = @tenant", ("tenant", rig.TenantId));
        Assert.Equal(0L, await ScalarAsync(
            rig.Factory,
            "SELECT count(*) FROM support.ticket_events WHERE tenant_id = @tenant AND details::text LIKE @key",
            ("tenant", rig.TenantId),
            ("key", $"%{key}%")));
        Assert.Equal(0L, await ScalarAsync(
            rig.Factory,
            "SELECT count(*) FROM audit.entries WHERE resource_id = @id AND (changed_fields::text LIKE @key OR changed_fields::text LIKE '%clientRequestId%')",
            ("id", ticketId),
            ("key", $"%{key}%")));
        Assert.Equal(1L, await ScalarAsync(
            rig.Factory,
            "SELECT count(*) FROM audit.entries WHERE action = @action AND resource_id = @id",
            ("action", SupportAuditActions.TicketCreated),
            ("id", ticketId)));
    }

    private async Task<Rig> RigAsync()
    {
        var factory = await host.StartTestAsync();
        var tenant = NewTenantId();
        var slug = await SeedPublishedTenantAsync(factory, tenant);
        return new Rig(factory, tenant, Guid.Parse(tenant), slug, await FirstTypeIdAsync(factory, tenant), await AnyCityIdAsync(factory));
    }

    // Each POST from its own visitor IP: the SupportPublicSubmit quota is 5 per 10 minutes per IP.
    private static async Task<HttpResponseMessage> FileAsync(Rig rig, object body)
    {
        using var client = VisitorClient(rig.Factory, NextIp());
        return await PostJsonAsync(client, PublicUrl(rig.Slug, "/tickets"), body);
    }

    private static async Task<HttpResponseMessage> LookupAsync(Rig rig, string number, string pin)
    {
        using var client = VisitorClient(rig.Factory, NextIp());
        return await PostJsonAsync(
            client, PublicUrl(rig.Slug, "/tickets/lookup"), new { number, pin, turnstileToken = FakeSupportCaptcha.ValidToken });
    }

    // A new published version of the form, as if the tenant saved it from the console.
    private static async Task PublishFormVersionAsync(Rig rig, int version)
    {
        await using var scope = rig.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISupportFormRepository>().Add(SupportFormVersion.Create(
            Guid.CreateVersion7(), rig.TenantId, version, true, true, true, true, true, true, "#00B4FF", "Cuéntanos tu caso.",
            "Otro mensaje de éxito.", Guid.CreateVersion7(), DateTimeOffset.UtcNow));
        await scope.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
    }

    // What the opening delivery wrote after the commit, cleared: as if the process died before recording it.
    private static Task ForgetDeliveryAsync(Rig rig) =>
        ExecuteAsync(
            rig.Factory,
            "UPDATE support.ticket_access SET last_sent_channel = NULL, last_failure_code = NULL, last_sent_at = NULL WHERE tenant_id = @tenant",
            ("tenant", rig.TenantId));

    private static string Email() => $"ana-{Guid.CreateVersion7():N}@example.com";

    private static Task<long> CountAsync(Rig rig, string sql) => ScalarAsync(rig.Factory, sql, ("tenant", rig.TenantId));

    private sealed record Rig(QepApiFactory Factory, string Tenant, Guid TenantId, string Slug, Guid TypeId, Guid CityId);
}
