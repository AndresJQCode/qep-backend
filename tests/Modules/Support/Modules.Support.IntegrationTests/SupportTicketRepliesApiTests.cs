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
        Assert.False(inside.GetProperty("thread").GetProperty("canAttach").GetBoolean());
        Assert.Equal("Thread", inside.GetProperty("defaultChannel").GetString());

        var (closed, closedConversation) = await TicketWithThreadAsync(scenario, DateTimeOffset.UtcNow.AddHours(-1), connection: Guid.CreateVersion7());
        Assert.Equal("template_not_approved", Reason(await OptionsAsync(scenario, Id(closed)), "thread"));
        _channels.ApprovedConnections[_channels.Threads[closedConversation].ConnectionId] = "Soporte";
        var outside = await OptionsAsync(scenario, Id(closed));
        Assert.True(Available(outside, "thread"));
        Assert.Equal("Template", outside.GetProperty("thread").GetProperty("delivery").GetString());

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
        return (await scenario.CreateFromConversationAsync(conversation), conversation);
    }

    private static async Task<JsonElement> OptionsAsync(SupportTicketScenario scenario, Guid ticketId)
    {
        var response = await scenario.Client.GetAsync($"{scenario.Url}/tickets/{ticketId}/reply-options", Ct);
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
    private async Task<Guid> SeedPendingReplyAsync(SupportTicketScenario scenario, Guid ticketId, Guid clientId, DateTimeOffset createdAt)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var reply = Modules.Support.Domain.SupportTicketReply.Start(
            scenario.TenantId, new Modules.Support.Domain.SupportTicketId(ticketId), clientId, Modules.Support.Domain.SupportReplyChannel.Thread,
            Modules.Support.Domain.SupportReplyDelivery.Text, "Hola", null, scenario.CallerMemberId, createdAt);
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
