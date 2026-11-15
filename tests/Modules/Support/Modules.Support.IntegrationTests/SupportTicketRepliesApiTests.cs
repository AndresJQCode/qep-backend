using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Support.Application;
using Modules.Tenancy.Domain;
using Testcontainers.PostgreSql;
using static Modules.Support.IntegrationTests.SupportApiHarness;
using static Modules.Support.IntegrationTests.SupportMessagingSeed;
using static Modules.Support.IntegrationTests.SupportTicketScenario;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// Soporte fase 2 (Tarea 10, plan E13 a E17): responder desde el ticket por el hilo, por la plantilla «Aviso de respuesta» o por correo, y
/// <c>reply-options</c>. Host propio por prueba: los dobles de los canales (<see cref="FakeSupportReplyChannels"/>,
/// <see cref="RecordingSupportReplyEmail"/>) anotan llamadas. Reemplazan los puertos de Soporte y no a Meta: el envío real por
/// Mensajería lo amarran <c>SupportReplyOutboundTests</c>.
/// </summary>
public sealed class SupportTicketRepliesApiTests : IAsyncLifetime
{
    private readonly FakeSupportReplyChannels _channels = new();
    private readonly RecordingSupportReplyEmail _email = new();
    private PostgreSqlContainer? _database;
    private QepApiFactory? _factory;

    private QepApiFactory Factory => _factory ?? throw new InvalidOperationException("The host is not started.");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        (_database, _factory) = await OpenOwnHostAsync(services =>
        {
            services.RemoveAll<ISupportReplyChannels>();
            services.AddSingleton<ISupportReplyChannels>(_channels);
            services.RemoveAll<ISupportReplyEmail>();
            services.AddSingleton<ISupportReplyEmail>(_email);
        });

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Fact]
    public async Task ReplyOptionsExplainEachChannel()
    {
        var scenario = await StartAsync(Factory);

        // Manual, sin conversación ni celular: sólo el correo.
        var byEmail = await scenario.CreateAsync(requester: new { firstNames = "Ana", email = "ana@example.com" });
        var emailOnly = await OptionsAsync(scenario, Id(byEmail));
        Assert.Equal("no_conversation", Reason(emailOnly, "thread"));
        Assert.Equal("no_mobile", Reason(emailOnly, "whatsAppTemplate"));
        Assert.True(Available(emailOnly, "email"));
        Assert.Equal("Email", emailOnly.GetProperty("defaultChannel").GetString());

        // Manual con celular: la plantilla por las conexiones con «Aviso de respuesta» aprobada.
        var connection = Guid.CreateVersion7();
        _channels.ApprovedConnections[connection] = "Ventas";
        var withMobile = await OptionsAsync(scenario, Id(await scenario.CreateAsync()));
        Assert.True(Available(withMobile, "whatsAppTemplate"));
        var option = Assert.Single(withMobile.GetProperty("whatsAppTemplate").GetProperty("connections").EnumerateArray());
        Assert.Equal(connection, option.GetProperty("id").GetGuid());
        Assert.Equal("Ventas", option.GetProperty("name").GetString());

        // Desde una conversación: ventana abierta ⇒ texto; cerrada ⇒ plantilla o el motivo.
        var (open, _) = await TicketWithThreadAsync(scenario, DateTimeOffset.UtcNow.AddHours(5));
        var inside = await OptionsAsync(scenario, Id(open));
        Assert.True(Available(inside, "thread"));
        Assert.Equal("Text", inside.GetProperty("thread").GetProperty("delivery").GetString());
        Assert.Equal(JsonValueKind.String, inside.GetProperty("thread").GetProperty("windowClosesAt").ValueKind);
        // Tarea 11 (E19): sin permiso de Mensajería no se puede abrir la sesión de subida, así que la pantalla no ofrece el adjunto.
        Assert.False(inside.GetProperty("thread").GetProperty("canAttach").GetBoolean());
        var withMessaging = CreateClient(
            Factory, scenario.SubjectId, scenario.Tenant, SupportPermissions.Read, SupportPermissions.Manage, SupportReplyAttachmentRules.UploadPermission);
        Assert.True((await OptionsAsync(withMessaging, scenario, Id(open))).GetProperty("thread").GetProperty("canAttach").GetBoolean());
        Assert.Equal("Thread", inside.GetProperty("defaultChannel").GetString());

        var (closed, closedConversation) = await TicketWithThreadAsync(scenario, DateTimeOffset.UtcNow.AddHours(-1), connection: Guid.CreateVersion7());
        Assert.Equal("template_not_approved", Reason(await OptionsAsync(scenario, Id(closed)), "thread"));
        _channels.ApprovedConnections[_channels.Threads[closedConversation].ConnectionId] = "Soporte";
        var outside = await OptionsAsync(scenario, Id(closed));
        Assert.True(Available(outside, "thread"));
        Assert.Equal("Template", outside.GetProperty("thread").GetProperty("delivery").GetString());
        // Fuera de la ventana sale la plantilla, que no lleva adjunto: tampoco con permiso de Mensajería.
        Assert.False((await OptionsAsync(withMessaging, scenario, Id(closed))).GetProperty("thread").GetProperty("canAttach").GetBoolean());

        var (blocked, _) = await TicketWithThreadAsync(scenario, DateTimeOffset.UtcNow.AddHours(5), blocked: true);
        Assert.Equal("contact_blocked", Reason(await OptionsAsync(scenario, Id(blocked)), "thread"));

        var (instagram, _) = await TicketWithThreadAsync(scenario, DateTimeOffset.UtcNow.AddHours(-1), channel: "Instagram");
        Assert.Equal("window_closed", Reason(await OptionsAsync(scenario, Id(instagram)), "thread"));

        // La conexión de la conversación no está activa: ni texto ni plantilla, aunque la ventana esté abierta.
        var (paused, pausedConversation) = await TicketWithThreadAsync(scenario, DateTimeOffset.UtcNow.AddHours(5));
        _channels.Threads[pausedConversation] = _channels.Threads[pausedConversation] with { ConnectionOperational = false };
        var down = await OptionsAsync(scenario, Id(paused));
        Assert.Equal("connection_unavailable", Reason(down, "thread"));
        Assert.Equal("connection_unavailable", Reason(down, "whatsAppTemplate"));

        // Sin Mensajería: ni hilo ni plantilla.
        var noMessaging = await StartAsync(Factory, TenantModuleKeys.All.Where(key => key != TenantModuleKeys.Messaging));
        var off = await OptionsAsync(noMessaging, Id(await noMessaging.CreateAsync()));
        Assert.Equal("messaging_disabled", Reason(off, "whatsAppTemplate"));
        Assert.True(Available(off, "email"));
    }

    [Fact]
    public async Task AThreadReplyInsideTheWindowIsSentAndPinned()
    {
        var scenario = await StartAsync(Factory);
        var (ticket, conversation) = await TicketWithThreadAsync(scenario, DateTimeOffset.UtcNow.AddHours(5));
        var message = await SeedMessageAsync(Factory, scenario.TenantId, conversation, DateTimeOffset.UtcNow, direction: Outbound, text: "Ya lo revisamos.");
        _channels.OnThread = send => new SupportChannelSendResult.Sent(message, send.ConversationId);

        var result = await ReplyCreatedAsync(scenario, ticket, new { clientId = Guid.CreateVersion7(), channel = "Thread", text = "Ya lo revisamos." });

        var reply = result.GetProperty("reply");
        Assert.Equal("Sent", reply.GetProperty("status").GetString());
        Assert.Equal("Text", reply.GetProperty("delivery").GetString());
        Assert.Equal(message, reply.GetProperty("messageId").GetGuid());
        Assert.Equal(scenario.CallerMemberId, reply.GetProperty("author").GetProperty("id").GetGuid());
        var send = Assert.Single(_channels.ThreadSends);
        Assert.Equal(reply.GetProperty("id").GetGuid(), send.ClientId);
        Assert.Equal(scenario.CallerMemberId, send.AuthorMemberId);
        Assert.Equal(
            "Pinned",
            await TextAsync(Factory, "SELECT state FROM support.ticket_messages WHERE message_id = @id", ("id", message)));
        var last = result.GetProperty("ticket").GetProperty("events").EnumerateArray().Last();
        Assert.Equal("ReplySent", last.GetProperty("kind").GetString());
        Assert.Equal("Thread", last.GetProperty("replyChannel").GetString());
        Assert.Equal(1, await CountAuditEntriesAsync(Factory, SupportAuditActions.TicketReplySent, Id(ticket).ToString()));
    }

    // Revisión final (I3): la respuesta lleva su clientId, así la pantalla puede retomar una que quedó Pending con el mismo valor.
    [Fact]
    public async Task TheReplyCarriesItsClientIdInTheResultAndInTheDetail()
    {
        var scenario = await StartAsync(Factory);
        var (ticket, _) = await TicketWithThreadAsync(scenario, DateTimeOffset.UtcNow.AddHours(5));
        var clientId = Guid.CreateVersion7();

        var result = await ReplyCreatedAsync(scenario, ticket, new { clientId, channel = "Thread", text = "Hola" });

        Assert.Equal(clientId, result.GetProperty("reply").GetProperty("clientId").GetGuid());
        var listed = Assert.Single(result.GetProperty("ticket").GetProperty("replies").EnumerateArray());
        Assert.Equal(clientId, listed.GetProperty("clientId").GetGuid());
        var detail = await scenario.DetailAsync(Id(ticket));
        Assert.Equal(clientId, Assert.Single(detail.GetProperty("replies").EnumerateArray()).GetProperty("clientId").GetGuid());
    }

    // Revisión final (M7): el mensaje se fija sólo si salió por la conversación del ticket. Uno que Mensajería mandó por otra no entra
    // al expediente de este ticket.
    [Fact]
    public async Task AMessageSentThroughAnotherConversationIsNotPinned()
    {
        var scenario = await StartAsync(Factory);
        var (ticket, _) = await TicketWithThreadAsync(scenario, DateTimeOffset.UtcNow.AddHours(5));
        var message = Guid.CreateVersion7();
        _channels.OnThread = _ => new SupportChannelSendResult.Sent(message, Guid.CreateVersion7());

        var result = await ReplyCreatedAsync(scenario, ticket, new { clientId = Guid.CreateVersion7(), channel = "Thread", text = "Hola" });

        Assert.Equal("Sent", result.GetProperty("reply").GetProperty("status").GetString());
        Assert.Equal(0, await ScalarAsync(Factory, "SELECT count(*) FROM support.ticket_messages WHERE message_id = @id", ("id", message)));
    }

    [Fact]
    public async Task AThreadReplyOutsideTheWindowGoesAsTheSupportTemplate()
    {
        var scenario = await StartAsync(Factory);
        var connection = Guid.CreateVersion7();
        _channels.ApprovedConnections[connection] = "Soporte";
        var (ticket, conversation) = await TicketWithThreadAsync(scenario, DateTimeOffset.UtcNow.AddHours(-2), connection: connection);

        var result = await ReplyCreatedAsync(scenario, ticket, new { clientId = Guid.CreateVersion7(), channel = "Thread", text = "Tu pedido sale mañana." });

        Assert.Equal("Template", result.GetProperty("reply").GetProperty("delivery").GetString());
        Assert.Empty(_channels.ThreadSends);
        var send = Assert.Single(_channels.TemplateSends);
        Assert.Equal(conversation, send.ConversationId);
        Assert.Null(send.WaId);
        Assert.Equal(ticket.GetProperty("number").GetString(), send.TicketNumber);
        Assert.Equal("Tu pedido sale mañana.", send.Text);
    }

    [Fact]
    public async Task WithoutAnApprovedTemplateOutsideTheWindowIs422()
    {
        var scenario = await StartAsync(Factory);
        var (ticket, _) = await TicketWithThreadAsync(scenario, DateTimeOffset.UtcNow.AddHours(-2));

        var response = await ReplyAsync(scenario, ticket, new { clientId = Guid.CreateVersion7(), channel = "Thread", text = "Hola" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("support.reply.template_not_approved", await CodeOfAsync(response));
        Assert.Empty(_channels.TemplateSends);
        Assert.Equal(0, await RepliesCountAsync(Id(ticket)));
    }

    [Fact]
    public async Task AWhatsAppTemplateReplyWithoutConversationCreatesAndFollowsIt()
    {
        var scenario = await StartAsync(Factory);
        var connection = Guid.CreateVersion7();
        _channels.ApprovedConnections[connection] = "Soporte";
        var ticket = await scenario.CreateAsync();
        var opened = await SeedConversationAsync(Factory, scenario.TenantId, waId: "573001234567", connectionId: connection);
        _channels.OnTemplate = _ => new SupportChannelSendResult.Sent(Guid.CreateVersion7(), opened);

        var result = await ReplyCreatedAsync(
            scenario, ticket, new { clientId = Guid.CreateVersion7(), channel = "WhatsAppTemplate", text = "Ya quedó resuelto", connectionId = connection });

        var send = Assert.Single(_channels.TemplateSends);
        Assert.Null(send.ConversationId);
        Assert.Equal(connection, send.ConnectionId);
        Assert.Equal("573001234567", send.WaId);
        Assert.Equal("Ana Gómez", send.RecipientName);
        // I1: la fila guarda el destino, para que un retomado salga al mismo wa_id aunque la foto cambie.
        Assert.Equal(
            "573001234567",
            await TextAsync(Factory, "SELECT target_wa_id FROM support.ticket_replies WHERE id = @id", ("id", send.ClientId)));
        var detail = result.GetProperty("ticket");
        Assert.Equal(opened, detail.GetProperty("conversationId").GetGuid());
        Assert.Equal("WhatsApp", detail.GetProperty("conversationChannel").GetString());
        Assert.True(detail.GetProperty("followsThread").GetBoolean());
        Assert.Contains(detail.GetProperty("events").EnumerateArray(), item => item.GetProperty("kind").GetString() == "ConversationLinked");
        Assert.Equal(1, await CountTicketEventsAsync(Factory, opened, Id(ticket), "TicketOpened"));
    }

    [Fact]
    public async Task AnEmailReplyGoesToTheRequesterEmail()
    {
        var scenario = await StartAsync(Factory);
        var ticket = await scenario.CreateAsync();

        var result = await ReplyCreatedAsync(scenario, ticket, new { clientId = Guid.CreateVersion7(), channel = "Email", text = "Te enviamos la factura." });

        Assert.Equal("Email", result.GetProperty("reply").GetProperty("delivery").GetString());
        Assert.Equal("Sent", result.GetProperty("reply").GetProperty("status").GetString());
        var email = Assert.Single(_email.Sent);
        Assert.Equal("ana@example.com", email.ToAddress);
        Assert.Equal(ticket.GetProperty("number").GetString(), email.TicketNumber);
        Assert.Equal("No me llegó la factura", email.TicketSubject);
        Assert.Equal("Te enviamos la factura.", email.Text);
        Assert.Equal("Support API Tests", email.TenantName);
    }

    [Fact]
    public async Task SendAndResolveResolvesOnlyWhenSent()
    {
        var scenario = await StartAsync(Factory);
        var sent = await scenario.CreateAsync();

        var resolved = await ReplyCreatedAsync(
            scenario, sent, new { clientId = Guid.CreateVersion7(), channel = "Email", text = "Listo.", resolve = true });

        Assert.Equal("Resolved", resolved.GetProperty("ticket").GetProperty("status").GetString());

        _email.Fail = true;
        var failing = await scenario.CreateAsync();
        var unresolved = await ReplyCreatedAsync(
            scenario, failing, new { clientId = Guid.CreateVersion7(), channel = "Email", text = "Listo.", resolve = true });

        Assert.Equal("Failed", unresolved.GetProperty("reply").GetProperty("status").GetString());
        Assert.Equal("New", unresolved.GetProperty("ticket").GetProperty("status").GetString());
        Assert.Equal(1, await CountAuditEntriesAsync(Factory, SupportAuditActions.TicketReplyFailed, Id(failing).ToString()));
    }

    [Fact]
    public async Task AFailedSendIs201WithTheFailedReply()
    {
        var scenario = await StartAsync(Factory);
        var (ticket, _) = await TicketWithThreadAsync(scenario, DateTimeOffset.UtcNow.AddHours(5));
        var failedMessage = Guid.CreateVersion7();
        _channels.OnThread = _ => new SupportChannelSendResult.Failed(failedMessage, "send_failed");

        var result = await ReplyCreatedAsync(scenario, ticket, new { clientId = Guid.CreateVersion7(), channel = "Thread", text = "Hola" });

        var reply = result.GetProperty("reply");
        Assert.Equal("Failed", reply.GetProperty("status").GetString());
        Assert.Equal("send_failed", reply.GetProperty("failureCode").GetString());
        // Revisión final (M4): el mensaje fallido queda en la fila (para ubicarlo en el hilo), pero no se fija en el expediente.
        Assert.Equal(failedMessage, reply.GetProperty("messageId").GetGuid());
        var listed = Assert.Single(result.GetProperty("ticket").GetProperty("replies").EnumerateArray());
        Assert.Equal("Failed", listed.GetProperty("status").GetString());
        Assert.Equal(0, await ScalarAsync(Factory, "SELECT count(*) FROM support.ticket_messages WHERE message_id = @id", ("id", failedMessage)));
    }

    // Review Focus 3: el mismo clientId otra vez devuelve la misma fila sin volver a mandar.
    [Fact]
    public async Task RetryingAReplyWithTheSameClientIdSendsOnce()
    {
        var scenario = await StartAsync(Factory);
        var (ticket, _) = await TicketWithThreadAsync(scenario, DateTimeOffset.UtcNow.AddHours(5));
        var body = new { clientId = Guid.CreateVersion7(), channel = "Thread", text = "Hola otra vez" };

        var first = await ReplyCreatedAsync(scenario, ticket, body);
        var current = await scenario.DetailAsync(Id(ticket));
        var second = await ReplyCreatedAsync(scenario, current, body);

        Assert.Single(_channels.ThreadSends);
        Assert.Equal(first.GetProperty("reply").GetProperty("id").GetGuid(), second.GetProperty("reply").GetProperty("id").GetGuid());
        Assert.Equal(1, await RepliesCountAsync(Id(ticket)));
    }

    // Ruling del controlador (fix round 1): la respuesta HTTP se perdió y la pantalla reintenta con el If-Match que tenía, ya viejo
    // porque la respuesta subió la versión. Recibe la fila guardada, no un 412, y no se manda otra vez.
    [Fact]
    public async Task ALostResponseRetryWithTheStaleIfMatchReturnsTheStoredReply()
    {
        var scenario = await StartAsync(Factory);
        var (ticket, _) = await TicketWithThreadAsync(scenario, DateTimeOffset.UtcNow.AddHours(5));
        var body = new { clientId = Guid.CreateVersion7(), channel = "Thread", text = "Hola" };

        var first = await ReplyCreatedAsync(scenario, ticket, body);
        Assert.True(Version(first.GetProperty("ticket")) > Version(ticket));
        var retry = await ReplyCreatedAsync(scenario, ticket, body);

        Assert.Single(_channels.ThreadSends);
        Assert.Equal(first.GetProperty("reply").GetProperty("id").GetGuid(), retry.GetProperty("reply").GetProperty("id").GetGuid());
        Assert.Equal("Sent", retry.GetProperty("reply").GetProperty("status").GetString());
        Assert.Equal(Version(first.GetProperty("ticket")), Version(retry.GetProperty("ticket")));

        // Una respuesta nueva con la versión vieja sí es 412.
        var stale = await ReplyAsync(scenario, ticket, new { clientId = Guid.CreateVersion7(), channel = "Thread", text = "Otra" });
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
    }

    // Review Focus 3, camino Pending: la fila se confirmó y el proceso cayó antes de marcarla. El reintento con el mismo clientId la
    // retoma: una sola llamada, con el id de la fila como clientId hacia Mensajería, y un solo ReplySent.
    [Fact]
    public async Task APendingReplyOlderThanAMinuteIsResumedOnce()
    {
        var scenario = await StartAsync(Factory);
        var (ticket, _) = await TicketWithThreadAsync(scenario, DateTimeOffset.UtcNow.AddHours(5));
        var clientId = Guid.CreateVersion7();
        var rowId = await SeedPendingReplyAsync(scenario, Id(ticket), clientId, DateTimeOffset.UtcNow.AddMinutes(-2));

        var result = await ReplyCreatedAsync(scenario, ticket, new { clientId, channel = "Thread", text = "Hola" });

        var send = Assert.Single(_channels.ThreadSends);
        Assert.Equal(rowId, send.ClientId);
        Assert.Equal(rowId, result.GetProperty("reply").GetProperty("id").GetGuid());
        Assert.Equal("Sent", result.GetProperty("reply").GetProperty("status").GetString());
        Assert.Equal(1, await ReplySentEventsAsync(Id(ticket)));
        Assert.Equal(1, await RepliesCountAsync(Id(ticket)));
    }

    // Ruling del controlador: una fila Pending de menos de un minuto la está mandando otro request con el mismo clientId. Se devuelve
    // tal cual, sin volver a mandar.
    [Fact]
    public async Task AFreshPendingReplyIsReturnedWithoutResending()
    {
        var scenario = await StartAsync(Factory);
        var (ticket, _) = await TicketWithThreadAsync(scenario, DateTimeOffset.UtcNow.AddHours(5));
        var clientId = Guid.CreateVersion7();
        var rowId = await SeedPendingReplyAsync(scenario, Id(ticket), clientId, DateTimeOffset.UtcNow);

        var result = await ReplyCreatedAsync(scenario, ticket, new { clientId, channel = "Thread", text = "Hola" });

        Assert.Empty(_channels.ThreadSends);
        Assert.Equal(rowId, result.GetProperty("reply").GetProperty("id").GetGuid());
        Assert.Equal("Pending", result.GetProperty("reply").GetProperty("status").GetString());
        Assert.Equal(0, await ReplySentEventsAsync(Id(ticket)));
    }

    // Mientras salía el mensaje, otro request terminó la misma respuesta (la marcó y subió la versión del ticket). El guardado de este
    // choca, recarga, ve la fila ya terminada y no escribe un segundo ReplySent ni otra auditoría.
    [Fact]
    public async Task AReplySettledByAnotherRequestDoesNotWriteASecondReplySent()
    {
        var scenario = await StartAsync(Factory);
        var (ticket, _) = await TicketWithThreadAsync(scenario, DateTimeOffset.UtcNow.AddHours(5));
        var message = Guid.CreateVersion7();
        _channels.OnThread = send =>
        {
            ExecuteAsync(
                Factory,
                """
                UPDATE support.ticket_replies SET status = 'Sent', message_id = @message, updated_at = now() WHERE id = @reply;
                UPDATE support.tickets SET version = version + 1 WHERE id = @ticket;
                """,
                ("message", message), ("reply", send.ClientId), ("ticket", Id(ticket))).GetAwaiter().GetResult();
            return new SupportChannelSendResult.Sent(message, send.ConversationId);
        };

        var result = await ReplyCreatedAsync(scenario, ticket, new { clientId = Guid.CreateVersion7(), channel = "Thread", text = "Hola" });

        Assert.Equal("Sent", result.GetProperty("reply").GetProperty("status").GetString());
        Assert.Equal(0, await ReplySentEventsAsync(Id(ticket)));
        Assert.Equal(0, await CountAuditEntriesAsync(Factory, SupportAuditActions.TicketReplySent, Id(ticket).ToString()));
    }

    [Fact]
    public async Task ATemplateTextWithLineBreaksIs422BeforeSending()
    {
        var scenario = await StartAsync(Factory);
        var connection = Guid.CreateVersion7();
        _channels.ApprovedConnections[connection] = "Soporte";
        var (ticket, _) = await TicketWithThreadAsync(scenario, DateTimeOffset.UtcNow.AddHours(-2), connection: connection);

        var response = await ReplyAsync(scenario, ticket, new { clientId = Guid.CreateVersion7(), channel = "Thread", text = "Hola\nAna" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("support.reply.template_text_invalid", await CodeOfAsync(response));
        Assert.Empty(_channels.TemplateSends);
        Assert.Equal(0, await RepliesCountAsync(Id(ticket)));
    }

    // Revisión del bloque C (CM1): una Pending vieja sale por lo que guardó su fila (canal, entrega y conexión), no por lo que traiga el
    // reintento: el envío ya se decidió, y otra entrega mandaría al cliente un mensaje distinto del que el ticket registra.
    [Fact]
    public async Task APendingReplyIsResumedThroughItsStoredChannelNotTheRetryOne()
    {
        var scenario = await StartAsync(Factory);
        // Ticket manual con la solicitante por defecto (Ana, con correo): la fila guardada es de correo y el reintento pide el hilo.
        // Uno desde una conversación sin cliente no tiene correo en la foto y el retomado quedaría Failed por eso, no por el canal.
        var ticket = await scenario.CreateAsync();
        var emailClient = Guid.CreateVersion7();
        var emailRow = await SeedPendingReplyAsync(
            scenario, Id(ticket), emailClient, DateTimeOffset.UtcNow.AddMinutes(-2),
            Modules.Support.Domain.SupportReplyChannel.Email, Modules.Support.Domain.SupportReplyDelivery.Email);

        var byEmail = await ReplyCreatedAsync(scenario, ticket, new { clientId = emailClient, channel = "Thread", text = "Otro texto" });

        Assert.Empty(_channels.ThreadSends);
        var email = Assert.Single(_email.Sent);
        Assert.Equal("Hola", email.Text);
        Assert.Equal((emailRow, "Email", "Sent"), (
            byEmail.GetProperty("reply").GetProperty("id").GetGuid(),
            byEmail.GetProperty("reply").GetProperty("channel").GetString(),
            byEmail.GetProperty("reply").GetProperty("status").GetString()));

        // La plantilla a un ticket sin conversación sale por la conexión guardada, no por la del reintento.
        var stored = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        _channels.ApprovedConnections[stored] = "Guardada";
        _channels.ApprovedConnections[other] = "Otra";
        var manual = await scenario.CreateAsync();
        var templateClient = Guid.CreateVersion7();
        await SeedPendingReplyAsync(
            scenario, Id(manual), templateClient, DateTimeOffset.UtcNow.AddMinutes(-2),
            Modules.Support.Domain.SupportReplyChannel.WhatsAppTemplate, Modules.Support.Domain.SupportReplyDelivery.Template, stored,
            targetWaId: "573001234567");
        _channels.OnTemplate = _ => new SupportChannelSendResult.Failed(null, "send_failed");

        await ReplyCreatedAsync(scenario, manual, new { clientId = templateClient, channel = "WhatsAppTemplate", text = "Hola", connectionId = other });

        Assert.Equal(stored, Assert.Single(_channels.TemplateSends).ConnectionId);
    }

    // Revisión final (I1): la plantilla al celular retomada sale al wa_id y por la conexión que guardó su fila, sin repetir las revisiones
    // de Soporte. La plantilla ya no aprobada y el celular editado no la marcan Failed: Mensajería, idempotente por clientId, devuelve lo
    // que ya salió (el doble responde Sent).
    [Fact]
    public async Task AResumedTemplateToPhoneUsesItsStoredTargetWithoutSupportChecks()
    {
        var scenario = await StartAsync(Factory);
        var ticket = await scenario.CreateAsync(requester: new { firstNames = "Ana", lastNames = "Gómez", email = "ana@example.com", mobilePhone = "+573009990000" });
        var connection = Guid.CreateVersion7();
        var opened = await SeedConversationAsync(Factory, scenario.TenantId, waId: "573001234567", connectionId: connection);
        var clientId = Guid.CreateVersion7();
        var rowId = await SeedPendingReplyAsync(
            scenario, Id(ticket), clientId, DateTimeOffset.UtcNow.AddMinutes(-2),
            Modules.Support.Domain.SupportReplyChannel.WhatsAppTemplate, Modules.Support.Domain.SupportReplyDelivery.Template, connection,
            targetWaId: "573001234567");
        _channels.OnTemplate = _ => new SupportChannelSendResult.Sent(Guid.CreateVersion7(), opened);

        var result = await ReplyCreatedAsync(scenario, ticket, new { clientId, channel = "WhatsAppTemplate", text = "Hola", connectionId = connection });

        var send = Assert.Single(_channels.TemplateSends);
        Assert.Equal((rowId, (Guid?)connection, "573001234567", (Guid?)null), (send.ClientId, send.ConnectionId, send.WaId, send.ConversationId));
        Assert.Equal((rowId, "Sent"), (
            result.GetProperty("reply").GetProperty("id").GetGuid(),
            result.GetProperty("reply").GetProperty("status").GetString()));
        Assert.Equal(opened, result.GetProperty("ticket").GetProperty("conversationId").GetGuid());
    }

    // CM2: un retomado que el canal rechaza, o que ya no tiene ruta (422), deja su fila Failed con el motivo: nunca la borra ni la deja
    // Pending, así la pantalla y el reintento ven lo mismo.
    [Fact]
    public async Task AResumeTheChannelRejectsMarksTheStoredRowFailed()
    {
        var scenario = await StartAsync(Factory);
        var (ticket, _) = await TicketWithThreadAsync(scenario, DateTimeOffset.UtcNow.AddHours(5));
        var clientId = Guid.CreateVersion7();
        var rowId = await SeedPendingReplyAsync(scenario, Id(ticket), clientId, DateTimeOffset.UtcNow.AddMinutes(-2));
        _channels.OnThread = _ => new SupportChannelSendResult.Rejected("window_closed");

        var result = await ReplyCreatedAsync(scenario, ticket, new { clientId, channel = "Thread", text = "Hola" });

        Assert.Equal((rowId, "Failed", "window_closed"), (
            result.GetProperty("reply").GetProperty("id").GetGuid(),
            result.GetProperty("reply").GetProperty("status").GetString(),
            result.GetProperty("reply").GetProperty("failureCode").GetString()));
        Assert.Equal(1, await RepliesCountAsync(Id(ticket)));
        Assert.Equal(1, await CountAuditEntriesAsync(Factory, SupportAuditActions.TicketReplyFailed, Id(ticket).ToString()));
    }

    [Fact]
    public async Task AResumeWithoutARouteMarksTheStoredRowFailed()
    {
        var scenario = await StartAsync(Factory);
        // Un ticket manual no tiene conversación: una fila Text guardada ya no tiene por dónde salir.
        var ticket = await scenario.CreateAsync();
        var clientId = Guid.CreateVersion7();
        var rowId = await SeedPendingReplyAsync(scenario, Id(ticket), clientId, DateTimeOffset.UtcNow.AddMinutes(-2));

        var result = await ReplyCreatedAsync(scenario, ticket, new { clientId, channel = "Thread", text = "Hola" });

        Assert.Empty(_channels.ThreadSends);
        Assert.Equal((rowId, "Failed", Modules.Support.Domain.SupportErrorCodes.NoConversation), (
            result.GetProperty("reply").GetProperty("id").GetGuid(),
            result.GetProperty("reply").GetProperty("status").GetString(),
            result.GetProperty("reply").GetProperty("failureCode").GetString()));
        Assert.Equal(1, await RepliesCountAsync(Id(ticket)));
    }

    // CM4: el retomado de una Pending vieja no mira el If-Match (el envío ya se decidió): uno viejo no es 412.
    [Fact]
    public async Task APendingResumeWithAStaleIfMatchIsNotRefused()
    {
        var scenario = await StartAsync(Factory);
        var (ticket, _) = await TicketWithThreadAsync(scenario, DateTimeOffset.UtcNow.AddHours(5));
        var clientId = Guid.CreateVersion7();
        var rowId = await SeedPendingReplyAsync(scenario, Id(ticket), clientId, DateTimeOffset.UtcNow.AddMinutes(-2));
        var moved = await scenario.MoveAsync(ticket, "Open");
        Assert.True(Version(moved) > Version(ticket));

        var result = await ReplyCreatedAsync(scenario, ticket, new { clientId, channel = "Thread", text = "Hola" });

        Assert.Equal(rowId, Assert.Single(_channels.ThreadSends).ClientId);
        Assert.Equal("Sent", result.GetProperty("reply").GetProperty("status").GetString());
    }

    // Revisión final (M2): la fila guarda su adjunto, y el retomado de una Pending manda exactamente lo guardado: el adjunto guardado y
    // no el del reintento, y ninguno si la fila no lo tenía.
    [Fact]
    public async Task AResumedPendingReplySendsItsStoredAttachmentNotTheRetryOne()
    {
        var scenario = await StartAsync(Factory);
        var (ticket, _) = await TicketWithThreadAsync(scenario, DateTimeOffset.UtcNow.AddHours(5));
        var withAttachment = Guid.CreateVersion7();
        var storedUpload = Guid.CreateVersion7();
        var attachedRow = await SeedPendingReplyAsync(
            scenario, Id(ticket), withAttachment, DateTimeOffset.UtcNow.AddMinutes(-2), uploadId: storedUpload, fileName: "guardada.pdf");
        var textOnly = Guid.CreateVersion7();
        var textRow = await SeedPendingReplyAsync(scenario, Id(ticket), textOnly, DateTimeOffset.UtcNow.AddMinutes(-2));

        await ReplyCreatedAsync(
            scenario, ticket, new { clientId = withAttachment, channel = "Thread", text = "Hola", uploadId = Guid.CreateVersion7(), fileName = "otra.pdf" });
        await ReplyCreatedAsync(
            scenario, ticket, new { clientId = textOnly, channel = "Thread", text = "Hola", uploadId = Guid.CreateVersion7(), fileName = "factura.pdf" });

        var sends = _channels.ThreadSends.ToArray();
        Assert.Equal(2, sends.Length);
        Assert.Equal((attachedRow, (Guid?)storedUpload, "guardada.pdf", "Hola"), (sends[0].ClientId, sends[0].UploadId, sends[0].FileName, sends[0].Text));
        Assert.Equal((textRow, (Guid?)null, (string?)null, "Hola"), (sends[1].ClientId, sends[1].UploadId, sends[1].FileName, sends[1].Text));
    }

    // Ruling del controlador (revisión del bloque D): Instagram no lleva pie en un adjunto, así que el texto de la respuesta se perdería.
    // Un adjunto a una conversación de Instagram es 422 antes de crear la fila, y reply-options no lo ofrece.
    [Fact]
    public async Task AnInstagramThreadDoesNotTakeAttachments()
    {
        var scenario = await StartAsync(Factory);
        var (ticket, _) = await TicketWithThreadAsync(scenario, DateTimeOffset.UtcNow.AddHours(5), channel: "Instagram");
        var withMessaging = CreateClient(
            Factory, scenario.SubjectId, scenario.Tenant, SupportPermissions.Read, SupportPermissions.Manage, SupportReplyAttachmentRules.UploadPermission);

        var options = await OptionsAsync(withMessaging, scenario, Id(ticket));
        var response = await ReplyAsync(
            scenario, ticket, new { clientId = Guid.CreateVersion7(), channel = "Thread", text = "Te adjunto la factura", uploadId = Guid.CreateVersion7(), fileName = "factura.pdf" });

        Assert.True(Available(options, "thread"));
        Assert.False(options.GetProperty("thread").GetProperty("canAttach").GetBoolean());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("support.reply.attachment_unavailable", await CodeOfAsync(response));
        Assert.Empty(_channels.ThreadSends);
        Assert.Equal(0, await RepliesCountAsync(Id(ticket)));
    }

    // Tarea 11 (E19): el adjunto viaja con su nombre hasta el canal, y el texto de la respuesta es su pie.
    [Fact]
    public async Task AThreadReplyWithAnAttachmentCarriesItToTheChannel()
    {
        var scenario = await StartAsync(Factory);
        var (ticket, conversation) = await TicketWithThreadAsync(scenario, DateTimeOffset.UtcNow.AddHours(5));
        var uploadId = Guid.CreateVersion7();

        var result = await ReplyCreatedAsync(
            scenario, ticket, new { clientId = Guid.CreateVersion7(), channel = "Thread", text = "Te adjunto la factura", uploadId, fileName = "factura.pdf" });

        var send = Assert.Single(_channels.ThreadSends);
        Assert.Equal((conversation, uploadId, "factura.pdf", "Te adjunto la factura"), (send.ConversationId, send.UploadId, send.FileName, send.Text));
        Assert.Equal("Sent", result.GetProperty("reply").GetProperty("status").GetString());
        // M2: la fila guarda el adjunto, para que un retomado mande el mismo.
        Assert.Equal(
            $"{uploadId}|factura.pdf",
            await TextAsync(
                Factory, "SELECT upload_id::text || '|' || file_name FROM support.ticket_replies WHERE id = @id", ("id", send.ClientId)));
    }

    // E19: un adjunto sólo sale por el hilo dentro de la ventana. Fuera, la respuesta sería la plantilla, que no lo lleva: 422 sin fila.
    [Fact]
    public async Task AnAttachmentOutsideTheWindowIs422()
    {
        var scenario = await StartAsync(Factory);
        var connection = Guid.CreateVersion7();
        _channels.ApprovedConnections[connection] = "Soporte";
        var (ticket, _) = await TicketWithThreadAsync(scenario, DateTimeOffset.UtcNow.AddHours(-2), connection: connection);

        var response = await ReplyAsync(
            scenario, ticket, new { clientId = Guid.CreateVersion7(), channel = "Thread", text = "Te adjunto la factura", uploadId = Guid.CreateVersion7(), fileName = "factura.pdf" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("support.reply.attachment_unavailable", await CodeOfAsync(response));
        Assert.Empty(_channels.ThreadSends);
        Assert.Empty(_channels.TemplateSends);
        Assert.Equal(0, await RepliesCountAsync(Id(ticket)));
    }

    // Fase 3 (plan F12): un anónimo sólo se responde por la página de consulta; la respuesta queda Sent sin llamar afuera.
    [Fact]
    public async Task AnAnonymousTicketRepliesOnlyByPortalAndTheReplyIsSentAtOnce()
    {
        var scenario = await StartAsync(Factory);
        var anonymous = await SupportTicketSeed.SaveAnonymousTicketAsync(Factory, scenario.TenantId);
        var ticket = await scenario.DetailAsync(anonymous.Id.Value);

        var byEmail = await ReplyAsync(scenario, ticket, new { clientId = Guid.CreateVersion7(), channel = "Email", text = "Hola." });
        var byThread = await ReplyAsync(scenario, ticket, new { clientId = Guid.CreateVersion7(), channel = "Thread", text = "Hola." });
        var result = await ReplyCreatedAsync(
            scenario, ticket, new { clientId = Guid.CreateVersion7(), channel = "Portal", text = "Ya revisamos tu caso.", resolve = true });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, byEmail.StatusCode);
        Assert.Equal("support.reply.portal_only", await CodeOfAsync(byEmail));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, byThread.StatusCode);
        Assert.Equal("support.reply.portal_only", await CodeOfAsync(byThread));
        var reply = result.GetProperty("reply");
        Assert.Equal("Portal", reply.GetProperty("channel").GetString());
        Assert.Equal("Portal", reply.GetProperty("delivery").GetString());
        Assert.Equal("Sent", reply.GetProperty("status").GetString());
        Assert.Equal("Resolved", result.GetProperty("ticket").GetProperty("status").GetString());
        Assert.True(result.GetProperty("ticket").GetProperty("isAnonymous").GetBoolean());
        Assert.Empty(_email.Sent);
        Assert.Empty(_channels.ThreadSends);
        Assert.Empty(_channels.TemplateSends);
        Assert.Equal(1, await ReplySentEventsAsync(anonymous.Id.Value));
    }

    // Review del Bloque A (Minor 2): Portal sobre un ticket con solicitante es 422, también con conversación (antes llegaba a la ruta
    // del hilo y el dominio lanzaba: un 500).
    [Fact]
    public async Task PortalIsRejectedOnANamedTicket()
    {
        var scenario = await StartAsync(Factory);
        var manual = await scenario.CreateAsync();
        var (withThread, _) = await TicketWithThreadAsync(scenario, DateTimeOffset.UtcNow.AddHours(5));

        var onManual = await ReplyAsync(scenario, manual, new { clientId = Guid.CreateVersion7(), channel = "Portal", text = "Hola." });
        var onThread = await ReplyAsync(scenario, withThread, new { clientId = Guid.CreateVersion7(), channel = "Portal", text = "Hola." });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, onManual.StatusCode);
        Assert.Equal("support.reply.portal_unavailable", await CodeOfAsync(onManual));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, onThread.StatusCode);
        Assert.Equal("support.reply.portal_unavailable", await CodeOfAsync(onThread));
        Assert.Equal(0, await RepliesCountAsync(Id(manual)));
        Assert.Equal(0, await RepliesCountAsync(Id(withThread)));
        Assert.Equal(
            "portal_unavailable", (await OptionsAsync(scenario, Id(manual))).GetProperty("portal").GetProperty("reason").GetString());
    }

    [Fact]
    public async Task ReplyOptionsOfAnAnonymousTicketOfferOnlyPortal()
    {
        var scenario = await StartAsync(Factory);
        var anonymous = await SupportTicketSeed.SaveAnonymousTicketAsync(Factory, scenario.TenantId);

        var options = await OptionsAsync(scenario, anonymous.Id.Value);

        Assert.True(Available(options, "portal"));
        Assert.False(Available(options, "thread"));
        Assert.False(Available(options, "whatsAppTemplate"));
        Assert.False(Available(options, "email"));
        Assert.Equal("portal_only", Reason(options, "thread"));
        Assert.Equal("portal_only", Reason(options, "email"));
        Assert.Equal("Portal", options.GetProperty("defaultChannel").GetString());
    }

    // Un ticket desde una conversación sembrada, y el estado del hilo que el doble devuelve para ella.
    private async Task<(JsonElement Ticket, Guid Conversation)> TicketWithThreadAsync(
        SupportTicketScenario scenario,
        DateTimeOffset windowClosesAt,
        string channel = "WhatsApp",
        bool blocked = false,
        Guid? connection = null)
    {
        var conversation = channel == "Instagram"
            ? await SeedConversationAsync(Factory, scenario.TenantId, channel: channel, waId: null, userId: $"1784{Random.Shared.NextInt64(1_000_000_000, 9_999_999_999)}", profileName: null, username: "ana.gomez")
            : await SeedConversationAsync(Factory, scenario.TenantId);
        _channels.Threads[conversation] = new SupportThreadStatus(
            conversation, channel, blocked, windowClosesAt, connection ?? Guid.CreateVersion7());
        var ticket = await scenario.CreateFromConversationAsync(conversation);
        // Fase 3 (Tarea 8): con la ventana abierta, el alta ya mandó por el hilo la información con el PIN. Estas pruebas miran sólo los
        // envíos de las respuestas, así que esa apertura (la amarran SupportOpeningApiTests) se descarta.
        _channels.ThreadSends.Clear();
        return (ticket, conversation);
    }

    private static Task<JsonElement> OptionsAsync(SupportTicketScenario scenario, Guid ticketId) =>
        OptionsAsync(scenario.Client, scenario, ticketId);

    private static async Task<JsonElement> OptionsAsync(HttpClient client, SupportTicketScenario scenario, Guid ticketId)
    {
        var response = await client.GetAsync($"{scenario.Url}/tickets/{ticketId}/reply-options", Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        return await JsonAsync(response);
    }

    private static string? Reason(JsonElement options, string channel) =>
        options.GetProperty(channel).GetProperty("reason").GetString();

    private static bool Available(JsonElement options, string channel) =>
        options.GetProperty(channel).GetProperty("available").GetBoolean();

    private static Task<HttpResponseMessage> ReplyAsync(SupportTicketScenario scenario, JsonElement ticket, object body) =>
        scenario.PostActionAsync($"tickets/{Id(ticket)}/replies", body, Version(ticket));

    private static async Task<JsonElement> ReplyCreatedAsync(SupportTicketScenario scenario, JsonElement ticket, object body)
    {
        var response = await ReplyAsync(scenario, ticket, body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await BodyAsync(response));
        return await JsonAsync(response);
    }

    // Una fila Pending como la deja un proceso que cayó entre confirmarla y marcarla, creada a la hora que se pide.
    private async Task<Guid> SeedPendingReplyAsync(
        SupportTicketScenario scenario,
        Guid ticketId,
        Guid clientId,
        DateTimeOffset createdAt,
        Modules.Support.Domain.SupportReplyChannel channel = Modules.Support.Domain.SupportReplyChannel.Thread,
        Modules.Support.Domain.SupportReplyDelivery delivery = Modules.Support.Domain.SupportReplyDelivery.Text,
        Guid? connectionId = null,
        string? targetWaId = null,
        Guid? uploadId = null,
        string? fileName = null)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var reply = Modules.Support.Domain.SupportTicketReply.Start(
            scenario.TenantId, new Modules.Support.Domain.SupportTicketId(ticketId), clientId, channel, delivery, "Hola", connectionId,
            scenario.CallerMemberId, createdAt, targetWaId, uploadId, fileName);
        scope.ServiceProvider.GetRequiredService<ISupportReplyRepository>().Add(reply);
        await scope.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
        return reply.Id;
    }

    private Task<long> ReplySentEventsAsync(Guid ticketId) =>
        ScalarAsync(
            Factory, "SELECT count(*) FROM support.ticket_events WHERE ticket_id = @id AND kind = 'ReplySent'", ("id", ticketId));

    private Task<long> RepliesCountAsync(Guid ticketId) =>
        ScalarAsync(Factory, "SELECT count(*) FROM support.ticket_replies WHERE ticket_id = @id", ("id", ticketId));
}
