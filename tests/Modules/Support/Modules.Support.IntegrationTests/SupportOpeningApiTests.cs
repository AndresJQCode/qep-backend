using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Support.Application;
using Modules.Support.Domain;
using static Modules.Support.IntegrationTests.SupportApiHarness;
using static Modules.Support.IntegrationTests.SupportMessagingSeed;
using static Modules.Support.IntegrationTests.SupportPublicHarness;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// Tarea 8 (plan F10, F11, F25, F26 y F29): la información del ticket con el PIN por su canal, para los tickets que abre un asesor
/// (desde una conversación o a mano) y para «Reenviar PIN». Host propio por clase (<see cref="SupportOpeningHost"/>): el doble de los
/// canales de Soporte anota cada envío por el hilo, el correo de apertura anota en vez de mandar, Turnstile es de mentira y el reloj del
/// tenant se mueve. Reemplazan los puertos de Soporte y no a Meta: el envío real por Mensajería lo amarran sus pruebas.
/// </summary>
public sealed class SupportOpeningApiTests(SupportOpeningHost host) : IClassFixture<SupportOpeningHost>
{
    private static readonly Regex SixDigits = new("clave ([0-9]{6})\\.$");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ATicketFromAConversationSendsItsInformationWithThePin()
    {
        var rig = await RigAsync();
        var conversation = await ConversationAsync(rig, windowOpen: true);

        var created = await CreateFromConversationAsync(rig, conversation);

        var send = Assert.Single(host.Channels.ThreadSends, item => item.ConversationId == conversation);
        Assert.Equal(rig.CallerMemberId, send.AuthorMemberId);
        Assert.Null(send.UploadId);
        var consultUrl = $"https://soporte.lulocrm.com/{rig.Slug}/consultar?radicado=SOP-000001";
        Assert.StartsWith("Registramos tu caso SOP-000001 (Petición general). Fecha límite de respuesta: ", send.Text, StringComparison.Ordinal);
        Assert.Contains($"Consulta su estado en {consultUrl} con la clave ", send.Text, StringComparison.Ordinal);
        var pin = SixDigits.Match(send.Text).Groups[1].Value;
        Assert.True(await PinMatchesAsync(rig, created, pin));
        var access = created.GetProperty("consultAccess");
        Assert.Equal("Thread", access.GetProperty("channel").GetString());
        Assert.Equal(JsonValueKind.Null, access.GetProperty("failureCode").ValueKind);
        // El PIN no va al detalle del ticket, ni a su traza.
        Assert.DoesNotContain(pin, created.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedOpeningStillCreatesTheTicket()
    {
        var rig = await RigAsync();
        var conversation = await ConversationAsync(rig, windowOpen: false);

        var created = await CreateFromConversationAsync(rig, conversation);

        Assert.DoesNotContain(host.Channels.ThreadSends, item => item.ConversationId == conversation);
        var access = created.GetProperty("consultAccess");
        Assert.Equal("Thread", access.GetProperty("channel").GetString());
        Assert.Equal(SupportChannelReasons.WindowClosed, access.GetProperty("failureCode").GetString());
        Assert.Equal(
            SupportChannelReasons.WindowClosed,
            (await rig.DetailAsync(Id(created))).GetProperty("consultAccess").GetProperty("failureCode").GetString());
        Assert.Equal(1L, await ScalarAsync(rig.Factory, "SELECT count(*) FROM support.ticket_access WHERE ticket_id = @id", ("id", Id(created))));
    }

    [Fact]
    public async Task AManualTicketByEmailEmailsTheOpening()
    {
        var rig = await RigAsync();
        var email = $"manual-{Guid.CreateVersion7():N}@example.com";

        var created = await rig.CreateManualAsync("Email", email);

        var sent = Assert.Single(host.Emails.To(email));
        Assert.Equal("SOP-000001", sent.TicketNumber);
        Assert.Equal($"https://soporte.lulocrm.com/{rig.Slug}/consultar?radicado=SOP-000001", sent.ConsultUrl);
        Assert.DoesNotContain(sent.Pin, sent.ConsultUrl, StringComparison.Ordinal);
        Assert.True(await PinMatchesAsync(rig, created, sent.Pin));
        Assert.Equal("Email", created.GetProperty("consultAccess").GetProperty("channel").GetString());
    }

    // Plan F26: un ticket manual por WhatsApp no tiene conversación; mandarle a un celular nuevo pide elegir conexión, y eso no cabe en
    // el alta. El PIN igual se emite (sólo su hash): queda para cuando el ticket tenga a dónde.
    [Fact]
    public async Task AManualTicketByWhatsAppSendsNothing()
    {
        var rig = await RigAsync();
        var email = $"whatsapp-{Guid.CreateVersion7():N}@example.com";
        var sendsBefore = host.Channels.ThreadSends.Count + host.Channels.TemplateSends.Count;

        var created = await rig.CreateManualAsync("WhatsAppTemplate", email);

        Assert.Empty(host.Emails.To(email));
        Assert.Equal(sendsBefore, host.Channels.ThreadSends.Count + host.Channels.TemplateSends.Count);
        Assert.Equal(1L, await ScalarAsync(rig.Factory, "SELECT count(*) FROM support.ticket_access WHERE ticket_id = @id", ("id", Id(created))));
        var access = created.GetProperty("consultAccess");
        Assert.Equal(JsonValueKind.Null, access.GetProperty("channel").ValueKind);
        Assert.Equal(JsonValueKind.Null, access.GetProperty("lastSentAt").ValueKind);
    }

    [Fact]
    public async Task ResendPinOfAConversationTicketGoesToItsThread()
    {
        var rig = await RigAsync();
        var conversation = await ConversationAsync(rig, windowOpen: true);
        var created = await CreateFromConversationAsync(rig, conversation);
        var first = Assert.Single(host.Channels.ThreadSends, item => item.ConversationId == conversation);
        host.Clock.UtcNow = ClockedSupportHost.Reception + SupportTicketAccess.ResendCooldown + TimeSpan.FromMinutes(1);

        using var visitor = VisitorClient(rig.Factory);
        var response = await PostJsonAsync(
            visitor,
            PublicUrl(rig.Slug, "/tickets/resend-pin"),
            new { number = "SOP-000001", email = "intruso@example.com", turnstileToken = FakeSupportCaptcha.ValidToken });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var sends = host.Channels.ThreadSends.Where(item => item.ConversationId == conversation).ToList();
        Assert.Equal(2, sends.Count);
        var second = sends[1];
        Assert.NotEqual(first.ClientId, second.ClientId);
        Assert.Equal(rig.CallerMemberId, second.AuthorMemberId);
        var pin = SixDigits.Match(second.Text).Groups[1].Value;
        Assert.True(await PinMatchesAsync(rig, created, pin));
        Assert.Empty(host.Emails.To("intruso@example.com"));
        Assert.Equal("Thread", await TextAsync(rig.Factory, "SELECT last_sent_channel FROM support.ticket_access WHERE ticket_id = @id", ("id", Id(created))));
    }

    // Revisión final, I2: sin el formulario publicado, el enlace de consulta responde 404. La apertura no sale (ni por el hilo ni por
    // correo) y queda anotada como form_unpublished; el PIN igual se emite (sólo su hash), para que publicar y reenviar funcione después.
    // El 201 llega y la versión del ticket no cambia.
    [Fact]
    public async Task AnUnpublishedFormSendsNoOpeningAndRecordsFormUnpublishedForAConversationTicket()
    {
        var rig = await RigAsync(published: false);
        var conversation = await ConversationAsync(rig, windowOpen: true);

        var created = await CreateFromConversationAsync(rig, conversation);

        Assert.DoesNotContain(host.Channels.ThreadSends, item => item.ConversationId == conversation);
        await AssertFormUnpublishedAsync(rig, created, "Thread");
    }

    [Fact]
    public async Task AnUnpublishedFormSendsNoOpeningAndRecordsFormUnpublishedForAManualEmailTicket()
    {
        var rig = await RigAsync(published: false);
        var email = $"sin-publicar-{Guid.CreateVersion7():N}@example.com";

        var created = await rig.CreateManualAsync("Email", email);

        Assert.Empty(host.Emails.To(email));
        await AssertFormUnpublishedAsync(rig, created, "Email");
    }

    // La apertura escribe sólo en ticket_access: la versión del 201 sigue sirviendo para el siguiente cambio del asesor.
    [Fact]
    public async Task TheCreationResponseVersionIsStillValidAfterTheOpening()
    {
        var rig = await RigAsync();
        var conversation = await ConversationAsync(rig, windowOpen: true);
        var created = await CreateFromConversationAsync(rig, conversation);
        Assert.Single(host.Channels.ThreadSends, item => item.ConversationId == conversation);

        var response = await PutAsync(
            rig.Client, $"{SupportUrl(rig.Tenant)}/tickets/{Id(created)}/status", new { status = "Open" },
            created.GetProperty("version").GetInt64());

        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
    }

    private static async Task AssertFormUnpublishedAsync(Rig rig, JsonElement created, string channel)
    {
        var access = created.GetProperty("consultAccess");
        Assert.Equal(channel, access.GetProperty("channel").GetString());
        Assert.Equal(SupportChannelReasons.FormUnpublished, access.GetProperty("failureCode").GetString());
        Assert.Equal("form_unpublished", await TextAsync(
            rig.Factory, "SELECT last_failure_code FROM support.ticket_access WHERE ticket_id = @id", ("id", Id(created))));
        Assert.False(string.IsNullOrEmpty(await TextAsync(
            rig.Factory, "SELECT pin_hash FROM support.ticket_access WHERE ticket_id = @id", ("id", Id(created)))));
        Assert.Equal(
            created.GetProperty("version").GetInt64(),
            await ScalarAsync(rig.Factory, "SELECT version FROM support.tickets WHERE id = @id", ("id", Id(created))));
    }

    private async Task<Rig> RigAsync(bool published = true)
    {
        var factory = await host.StartTestAsync();
        var tenant = NewTenantId();
        var slug = await SeedPublishedTenantAsync(factory, tenant, published: published);
        var subjectId = Guid.CreateVersion7().ToString();
        var caller = await SeedCallerMembershipAsync(factory, "Zoe Llamante", "admin", tenant, subjectId);
        var client = CreateClient(factory, subjectId, tenant, SupportPermissions.Read, SupportPermissions.Manage);
        return new Rig(factory, tenant, slug, client, caller);
    }

    private async Task<Guid> ConversationAsync(Rig rig, bool windowOpen)
    {
        var conversation = await SeedConversationAsync(rig.Factory, rig.TenantId);
        var closesAt = windowOpen ? ClockedSupportHost.Reception.AddHours(5) : ClockedSupportHost.Reception.AddMinutes(-5);
        host.Channels.Threads[conversation] = new SupportThreadStatus(conversation, "WhatsApp", false, closesAt, Guid.CreateVersion7());
        return conversation;
    }

    private static async Task<JsonElement> CreateFromConversationAsync(Rig rig, Guid conversation)
    {
        var response = await rig.Client.PostAsJsonAsync(
            $"{SupportUrl(rig.Tenant)}/conversations/{conversation}/tickets",
            new
            {
                ticketTypeId = await FirstTypeIdAsync(rig.Factory, rig.Tenant),
                subject = "Pedido sin llegar",
                description = "El pedido de la semana pasada no ha llegado.",
            },
            Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await BodyAsync(response));
        return await JsonAsync(response);
    }

    private static async Task<bool> PinMatchesAsync(Rig rig, JsonElement ticket, string pin)
    {
        var hash = await TextAsync(rig.Factory, "SELECT pin_hash FROM support.ticket_access WHERE ticket_id = @id", ("id", Id(ticket)));
        return rig.Factory.Services.GetRequiredService<ISupportPinHasher>().Verify(hash, pin);
    }

    private static Guid Id(JsonElement ticket) => ticket.GetProperty("id").GetGuid();

    private sealed record Rig(QepApiFactory Factory, string Tenant, string Slug, HttpClient Client, Guid CallerMemberId)
    {
        public Guid TenantId => Guid.Parse(Tenant);

        public async Task<JsonElement> CreateManualAsync(string replyChannel, string email)
        {
            var response = await Client.PostAsJsonAsync(
                $"{SupportUrl(Tenant)}/tickets",
                new
                {
                    ticketTypeId = await FirstTypeIdAsync(Factory, Tenant),
                    subject = "No me llegó la factura",
                    description = "Pedí la factura de octubre y no ha llegado.",
                    manualMedium = "Phone",
                    replyChannel,
                    requester = new { firstNames = "Ana", lastNames = "Gómez", email, mobilePhone = "+573001234567" },
                },
                Ct);
            Assert.True(response.StatusCode == HttpStatusCode.Created, await BodyAsync(response));
            return await JsonAsync(response);
        }

        public async Task<JsonElement> DetailAsync(Guid ticketId)
        {
            var response = await Client.GetAsync($"{SupportUrl(Tenant)}/tickets/{ticketId}", Ct);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
            return await JsonAsync(response);
        }
    }
}

/// <summary><see cref="SupportPublicHost"/> con el doble de los canales de Soporte: anota cada envío por el hilo y el estado de cada
/// conversación lo fija la prueba.</summary>
public sealed class SupportOpeningHost : SupportPublicHost
{
    internal FakeSupportReplyChannels Channels { get; } = new();

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.RemoveAll<ISupportReplyChannels>();
        services.AddSingleton<ISupportReplyChannels>(Channels);
    }
}
