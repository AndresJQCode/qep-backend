using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Persistence;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>
/// Soporte fase 2 (plan E12 y E13): responder un ticket desde Soporte por el hilo (texto dentro de la ventana) o por la plantilla
/// «Aviso de respuesta». Es el camino real de producción de <see cref="ISupportReplyOutbound"/>, resuelto de un scope como lo hará el
/// adaptador de Soporte. No pasa por las guardas de la bandeja: no toma la conversación, no la reabre ni resta no leídos; sí liquida por
/// la tabla de Meta, que apaga <c>history_managed</c>. Host propio por prueba: el doble de Meta anota llamadas.
/// </summary>
public sealed class SupportReplyOutboundTests
{
    private const string Phone = "573001234567";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConnectionId, Guid ConversationId, Guid MemberId);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        using (var anonymous = factory.CreateClient())
        {
            await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", Phone, "w1", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, "hola"));
            await DrainDeliveriesAsync(factory);
        }

        var conversationId = await ScalarAsync<Guid>(connectionString, "SELECT id FROM messaging.conversations");
        var memberId = await OwnerMembershipIdAsync(connectionString, tenant);
        ScriptMetaOk(factory.MetaHandler);
        return new Fixture(factory, connectionString, tenant, connectionId, conversationId, memberId);
    }

    private static void ScriptMetaOk(FakeMetaGraphHandler meta) =>
        meta.Respond("/111/messages$", HttpStatusCode.OK,
            """{"messaging_product":"whatsapp","contacts":[{"input":"573001234567","wa_id":"573001234567"}],"messages":[{"id":"wamid.support"}]}""");

    private static MetaRequest[] Sends(Fixture f) =>
        f.Factory.MetaHandler.Requests
            .Where(request => request.Uri!.AbsolutePath.EndsWith("/111/messages", StringComparison.Ordinal) && !IsReadReceipt(request))
            .ToArray();

    private static Task<long> OutboundRowsAsync(Fixture f) =>
        CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 2");

    private static SupportThreadReplySend Thread(Fixture f, string text = "Ya revisamos tu caso.", Guid? clientId = null, Guid? conversationId = null) =>
        new(f.Tenant.TenantId, conversationId ?? f.ConversationId, clientId ?? Guid.CreateVersion7(), f.MemberId, text, UploadId: null);

    private static SupportTemplateReplySend Template(
        Fixture f, Guid? conversationId, string text = "Ya quedó resuelto", Guid? connectionId = null, string? waId = null, Guid? clientId = null) =>
        new(f.Tenant.TenantId, conversationId, connectionId, waId, CustomerId: null, "Ana Pérez", clientId ?? Guid.CreateVersion7(), f.MemberId,
            "SOP-000127", text);

    private static async Task<T> WithOutboundAsync<T>(Fixture f, Func<ISupportReplyOutbound, Task<T>> action)
    {
        using var scope = f.Factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<ISupportReplyOutbound>());
    }

    private static Task<SupportReplySendResult> SendThreadAsync(Fixture f, SupportThreadReplySend send) =>
        WithOutboundAsync(f, outbound => outbound.SendThreadAsync(send, Ct));

    private static Task<SupportReplySendResult> SendTemplateAsync(Fixture f, SupportTemplateReplySend send) =>
        WithOutboundAsync(f, outbound => outbound.SendTemplateAsync(send, Ct));

    /// <summary>La fila <c>SupportReply</c> aprobada de la conexión, con el texto base: lo que dejaría el aprovisionamiento ya aprobado por Meta.</summary>
    private static async Task SeedApprovedTemplateAsync(Fixture f)
    {
        using var scope = f.Factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        var name = SupportReplyTemplateDefinition.Name;
        dbContext.MessageTemplates.Add(MessageTemplate.Pending(
            f.Tenant.TenantId, f.ConnectionId, "901", name, SupportReplyTemplateDefinition.Language, SupportReplyTemplateDefinition.Category,
            TemplateComponentsBuilder.Named, MessageTemplateStatuses.Approved,
            TemplateComponentsBuilder.Components(SupportReplyTemplateDefinition.Draft(name, SupportReplyTemplateDefinition.BaseBody)).ToJsonString(),
            MessageTemplateKind.SupportReply, f.MemberId, DateTimeOffset.UtcNow));
        await dbContext.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task AThreadReplyInsideTheWindowIsSentAsTheAuthor()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var sent = Assert.IsType<SupportReplySendResult.Sent>(await SendThreadAsync(f, Thread(f)));

        Assert.Equal(f.ConversationId, sent.ConversationId);
        Assert.Equal($"1|{f.MemberId}|1|Ya revisamos tu caso.|wamid.support", await ScalarAsync<string>(f.ConnectionString,
            "SELECT kind || '|' || sent_by_member_id || '|' || status || '|' || text || '|' || wamid FROM messaging.messages WHERE id = @id",
            ("id", sent.MessageId)));
        var call = Assert.Single(Sends(f));
        using var body = JsonDocument.Parse(call.Body!);
        Assert.Equal("text", body.RootElement.GetProperty("type").GetString());
        Assert.Equal("Ya revisamos tu caso.", body.RootElement.GetProperty("text").GetProperty("body").GetString());

        // La foto de la conversación para el ticket: ventana abierta, sin bloqueo, por su conexión.
        var thread = await WithOutboundAsync(f, outbound => outbound.FindThreadAsync(f.Tenant.TenantId, f.ConversationId, Ct));
        Assert.NotNull(thread);
        Assert.Equal(("WhatsApp", false, f.ConnectionId), (thread.Channel, thread.Blocked, thread.ConnectionId));
        Assert.True(thread.WindowClosesAt > DateTimeOffset.UtcNow.AddHours(23));
    }

    [Fact]
    public async Task AThreadOfAnotherTenantIsNotFound()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var other = await RegisterTenantAsync(f.Factory);

        var thread = await WithOutboundAsync(f, outbound => outbound.FindThreadAsync(other.TenantId, f.ConversationId, Ct));
        var result = await SendThreadAsync(f, Thread(f) with { TenantId = other.TenantId });

        Assert.Null(thread);
        Assert.Equal(new SupportReplySendResult.Rejected("conversation_not_found"), result);
        Assert.Empty(Sends(f));
    }

    [Fact]
    public async Task AThreadReplyOutsideTheWindowIsRejectedWithoutARow()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET last_inbound_at = now() - interval '25 hours'");

        var result = await SendThreadAsync(f, Thread(f));

        Assert.Equal(new SupportReplySendResult.Rejected("window_closed"), result);
        Assert.Equal(0L, await OutboundRowsAsync(f));
        Assert.Empty(Sends(f));
    }

    [Fact]
    public async Task ABlockedContactIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await SeedApprovedTemplateAsync(f);
        await ExecuteAsync(f.ConnectionString,
            "UPDATE messaging.conversations SET blocked_at = now(), blocked_by_member_id = @member, blocked_wa_id = @phone, version = version + 1",
            ("member", f.MemberId), ("phone", Phone));

        var thread = await SendThreadAsync(f, Thread(f));
        var template = await SendTemplateAsync(f, Template(f, f.ConversationId));
        // Por teléfono, la conversación que encuentra el opener es la misma bloqueada.
        var byPhone = await SendTemplateAsync(f, Template(f, conversationId: null, connectionId: f.ConnectionId, waId: Phone));

        Assert.Equal(new SupportReplySendResult.Rejected("contact_blocked"), thread);
        Assert.Equal(new SupportReplySendResult.Rejected("contact_blocked"), template);
        Assert.Equal(new SupportReplySendResult.Rejected("contact_blocked"), byPhone);
        Assert.Equal(0L, await OutboundRowsAsync(f));
        Assert.Empty(Sends(f));
    }

    [Fact]
    public async Task RetryingAReplyWithTheSameClientIdSendsOnce()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var clientId = Guid.CreateVersion7();

        var first = Assert.IsType<SupportReplySendResult.Sent>(await SendThreadAsync(f, Thread(f, clientId: clientId)));
        // El reintento llega con la ventana ya cerrada: lo que salió, salió, antes de mirar la ventana.
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET last_inbound_at = now() - interval '25 hours'");
        var second = Assert.IsType<SupportReplySendResult.Sent>(await SendThreadAsync(f, Thread(f, clientId: clientId)));
        var concurrent = await Task.WhenAll(
            SendThreadAsync(f, Thread(f, clientId: clientId)), SendThreadAsync(f, Thread(f, clientId: clientId)));

        Assert.Equal(first.MessageId, second.MessageId);
        Assert.All(concurrent, result => Assert.Equal(first.MessageId, Assert.IsType<SupportReplySendResult.Sent>(result).MessageId));
        Assert.Single(Sends(f));
        Assert.Equal(1L, await OutboundRowsAsync(f));
    }

    [Fact]
    public async Task ATemplateReplyToAConversationWithoutWaIdUsesItsUserId()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await SeedApprovedTemplateAsync(f);
        var bsuidOnly = await SeedBsuidConversationAsync(f.Factory, f.ConnectionString, f.Tenant.TenantId, f.ConnectionId, "CO.9876543210", waId: null);

        var sent = Assert.IsType<SupportReplySendResult.Sent>(await SendTemplateAsync(f, Template(f, bsuidOnly)));

        Assert.Equal(bsuidOnly, sent.ConversationId);
        var call = Assert.Single(Sends(f));
        using var body = JsonDocument.Parse(call.Body!);
        Assert.Equal("CO.9876543210", body.RootElement.GetProperty("recipient").GetString());
        Assert.False(body.RootElement.TryGetProperty("to", out JsonElement ignored));
        var template = body.RootElement.GetProperty("template");
        Assert.Equal("qep_soporte_respuesta", template.GetProperty("name").GetString());
        var component = Assert.Single(template.GetProperty("components").EnumerateArray());
        Assert.Equal(
            [("nombre_contacto", "Ana Pérez"), ("radicado", "SOP-000127"), ("respuesta", "Ya quedó resuelto")],
            component.GetProperty("parameters").EnumerateArray()
                .Select(item => (item.GetProperty("parameter_name").GetString()!, item.GetProperty("text").GetString()!)));
        Assert.Equal(
            $"{bsuidOnly}|{f.MemberId}|11|1|Hola Ana Pérez, te escribimos sobre tu caso SOP-000127: Ya quedó resuelto. Si tienes dudas, responde este mensaje.",
            await ScalarAsync<string>(f.ConnectionString,
                "SELECT conversation_id || '|' || sent_by_member_id || '|' || kind || '|' || status || '|' || text FROM messaging.messages WHERE id = @id",
                ("id", sent.MessageId)));
    }

    [Fact]
    public async Task ATemplateReplyWithoutConversationOpensOneByPhone()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await SeedApprovedTemplateAsync(f);
        const string newPhone = "573009998877";

        var sent = Assert.IsType<SupportReplySendResult.Sent>(
            await SendTemplateAsync(f, Template(f, conversationId: null, connectionId: f.ConnectionId, waId: newPhone)));

        Assert.NotEqual(f.ConversationId, sent.ConversationId);
        Assert.Equal($"Open||{newPhone}|", await ScalarAsync<string>(f.ConnectionString,
            """
            SELECT status || '|' || coalesce(assigned_member_id::text, '') || '|' || wa_id || '|' || coalesce(user_id, '')
            FROM messaging.conversations WHERE id = @id
            """,
            ("id", sent.ConversationId)));
        var call = Assert.Single(Sends(f));
        using var body = JsonDocument.Parse(call.Body!);
        Assert.Equal(newPhone, body.RootElement.GetProperty("to").GetString());
        Assert.Equal($"{f.MemberId}|11|1|qep_soporte_respuesta", await ScalarAsync<string>(f.ConnectionString,
            "SELECT sent_by_member_id || '|' || kind || '|' || status || '|' || (details->'template'->>'name') FROM messaging.messages WHERE id = @id",
            ("id", sent.MessageId)));
    }

    [Fact]
    public async Task ATemplateReplyWithNewLinesIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await SeedApprovedTemplateAsync(f);

        var toConversation = await SendTemplateAsync(f, Template(f, f.ConversationId, text: "Hola.\nYa quedó."));
        var byPhone = await SendTemplateAsync(f, Template(f, conversationId: null, text: "Muchos     espacios", connectionId: f.ConnectionId, waId: "573009998877"));

        Assert.Equal(new SupportReplySendResult.Rejected("template_text_invalid"), toConversation);
        Assert.Equal(new SupportReplySendResult.Rejected("template_text_invalid"), byPhone);
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations"));
        Assert.Equal(0L, await OutboundRowsAsync(f));
        Assert.Empty(Sends(f));
    }

    [Fact]
    public async Task WithoutAnApprovedTemplateTheTemplateReplyIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var result = await SendTemplateAsync(f, Template(f, f.ConversationId));

        Assert.Equal(new SupportReplySendResult.Rejected("template_not_approved"), result);
        Assert.Empty(Sends(f));
    }

    [Fact]
    public async Task MetaRejectingTheReplyLeavesAFailedRowInTheThread()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        f.Factory.MetaHandler.Reset();
        f.Factory.MetaHandler.Respond("/111/messages$", HttpStatusCode.BadRequest, FakeMetaGraphHandler.GraphError(131026));

        var failed = Assert.IsType<SupportReplySendResult.Failed>(await SendThreadAsync(f, Thread(f)));

        Assert.Equal("send_failed", failed.Code);
        Assert.Equal("4|131026", await ScalarAsync<string>(f.ConnectionString,
            "SELECT status || '|' || failure_code FROM messaging.messages WHERE id = @id", ("id", failed.MessageId!.Value)));
    }

    [Fact]
    public async Task ASupportReplyDoesNotTakeNorReopenTheConversation()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET status = 'Resolved', unread_count = 2");
        var version = await ScalarAsync<long>(f.ConnectionString, "SELECT version FROM messaging.conversations");

        Assert.IsType<SupportReplySendResult.Sent>(await SendThreadAsync(f, Thread(f)));

        // Sin asesor, sigue resuelta, los no leídos intactos (seen: 0) y sin subir la versión.
        Assert.Equal($"Resolved||2|{version}", await ScalarAsync<string>(f.ConnectionString,
            "SELECT status || '|' || coalesce(assigned_member_id::text, '') || '|' || unread_count || '|' || version FROM messaging.conversations"));
    }

    // Review Focus 4: si la respuesta no apaga history_managed, la siguiente parte del historial le pisa el estado a la conversación.
    [Fact]
    public async Task ASupportReplyTurnsOffHistoryManaged()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await SeedApprovedTemplateAsync(f);
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET history_managed = true");

        Assert.IsType<SupportReplySendResult.Sent>(await SendThreadAsync(f, Thread(f)));
        Assert.False(await ScalarAsync<bool>(f.ConnectionString, "SELECT history_managed FROM messaging.conversations WHERE id = @id", ("id", f.ConversationId)));

        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET history_managed = true");
        Assert.IsType<SupportReplySendResult.Sent>(await SendTemplateAsync(f, Template(f, f.ConversationId)));
        Assert.False(await ScalarAsync<bool>(f.ConnectionString, "SELECT history_managed FROM messaging.conversations WHERE id = @id", ("id", f.ConversationId)));
    }

    [Fact]
    public async Task AnInstagramReplyOutsideTheWindowIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedInstagramConnectionAsync(factory, tenant.TenantId, "Instagram Ventas", InstagramPayloads.DefaultAccountId);
        factory.MetaHandler.Respond($@"^/v24\.0/{InstagramPayloads.DefaultUserId}\?", HttpStatusCode.OK,
            """{"name":"Laura Pérez","username":"laura.perez","id":"1234567890123456"}""");
        using (var anonymous = factory.CreateClient())
        {
            await PostInstagramWebhookAsync(anonymous, InstagramPayloads.Text("m1", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, "hola"));
            await DrainDeliveriesAsync(factory);
        }

        var conversationId = await ScalarAsync<Guid>(connectionString, "SELECT id FROM messaging.conversations");
        await ExecuteAsync(connectionString, "UPDATE messaging.conversations SET last_inbound_at = now() - interval '25 hours'");
        var memberId = await OwnerMembershipIdAsync(connectionString, tenant);
        using var scope = factory.Services.CreateScope();
        var outbound = scope.ServiceProvider.GetRequiredService<ISupportReplyOutbound>();

        var thread = await outbound.FindThreadAsync(tenant.TenantId, conversationId, Ct);
        var text = await outbound.SendThreadAsync(
            new SupportThreadReplySend(tenant.TenantId, conversationId, Guid.CreateVersion7(), memberId, "Ya quedó.", UploadId: null), Ct);
        var template = await outbound.SendTemplateAsync(
            new SupportTemplateReplySend(tenant.TenantId, conversationId, null, null, null, "Laura", Guid.CreateVersion7(), memberId, "SOP-000001", "Ya quedó."),
            Ct);

        Assert.Equal(("Instagram", connectionId), (thread!.Channel, thread.ConnectionId));
        Assert.Equal(new SupportReplySendResult.Rejected("window_closed"), text);
        // Instagram no tiene plantillas.
        Assert.Equal(new SupportReplySendResult.Rejected("channel_unsupported"), template);
        Assert.Equal(0L, await CountAsync(connectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 2"));
    }

    /// <summary>Meta retiene la primera llamada de envío hasta que la prueba la suelte (copia de <c>ExternalSendApiTests.HeldMeta</c>): así
    /// el segundo envío llega mientras el primero tiene el reclamo abierto.</summary>
    private sealed class HeldMeta(Func<int, HttpResponseMessage> answer)
    {
        private int _calls;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls => Volatile.Read(ref _calls);

        public HttpResponseMessage Respond()
        {
            var call = Interlocked.Increment(ref _calls);
            if (call == 1)
            {
                Entered.TrySetResult();
                Assert.True(Released.Task.Wait(TimeSpan.FromSeconds(60)), "La prueba nunca soltó la primera llamada a Meta.");
            }

            return answer(call);
        }
    }

    private static async Task WaitForABlockedBackendAsync(string connectionString)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await CountAsync(connectionString, "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'") > 0)
            {
                return;
            }

            await Task.Delay(50, Ct);
        }

        Assert.Fail("El segundo envío nunca quedó esperando el candado de la fila.");
    }

    // Review Focus 3: dos envíos en vuelo con el mismo clientId. El segundo llega con el primero todavía en Meta, espera el candado del
    // reclamo y sale por TakeInFlightAsync con la misma fila, sin otra llamada.
    [Fact]
    public async Task TwoRepliesInFlightWithTheSameClientIdSendOnce()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        f.Factory.MetaHandler.Reset();
        var held = new HeldMeta(call => FakeMetaGraphHandler.Json(HttpStatusCode.OK,
            """{"messaging_product":"whatsapp","contacts":[{"input":"573001234567","wa_id":"573001234567"}],"messages":[{"id":"wamid.held"}]}"""));
        f.Factory.MetaHandler.RespondWith("/111/messages$", request => held.Respond());
        var clientId = Guid.CreateVersion7();

        var first = SendThreadAsync(f, Thread(f, clientId: clientId));
        await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        var second = SendThreadAsync(f, Thread(f, clientId: clientId));
        await WaitForABlockedBackendAsync(f.ConnectionString);
        held.Released.TrySetResult();
        var results = await Task.WhenAll(first, second);

        var ids = results.Select(result => Assert.IsType<SupportReplySendResult.Sent>(result).MessageId).ToArray();
        Assert.Equal(ids[0], ids[1]);
        Assert.Equal(1, held.Calls);
        Assert.Equal(1L, await OutboundRowsAsync(f));
    }

    // Sin conversación en la respuesta, el reintento la busca por teléfono sin crearla y devuelve lo ya enviado antes de mirar la plantilla
    // o la conexión, que pudieron cambiar después del envío.
    [Fact]
    public async Task RetryingATemplateReplyByPhoneReturnsTheSentMessageEvenIfTheTemplateOrConnectionChanged()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await SeedApprovedTemplateAsync(f);
        var clientId = Guid.CreateVersion7();
        var send = Template(f, conversationId: null, connectionId: f.ConnectionId, waId: "573009998877", clientId: clientId);
        var first = Assert.IsType<SupportReplySendResult.Sent>(await SendTemplateAsync(f, send));

        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.message_templates SET status = 'PAUSED' WHERE kind = 'SupportReply'");
        var afterPause = Assert.IsType<SupportReplySendResult.Sent>(await SendTemplateAsync(f, send));
        await ExecuteAsync(f.ConnectionString, "UPDATE integrations.connections SET status = 'Paused'");
        var afterConnectionPaused = Assert.IsType<SupportReplySendResult.Sent>(await SendTemplateAsync(f, send));

        Assert.Equal(first, afterPause);
        Assert.Equal(first, afterConnectionPaused);
        Assert.Single(Sends(f));
        Assert.Equal(2L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations"));
    }

    [Theory]
    [InlineData("Ana\nPérez", "SOP-000127")]
    [InlineData("Ana      Pérez", "SOP-000127")]
    [InlineData("   ", "SOP-000127")]
    [InlineData("Ana Pérez", "")]
    [InlineData("Ana Pérez", "SOP-\r000127")]
    public async Task AnInvalidRecipientNameOrCaseNumberIsRecipientInvalid(string recipientName, string ticketNumber)
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await SeedApprovedTemplateAsync(f);

        var toConversation = await SendTemplateAsync(f, Template(f, f.ConversationId) with { RecipientName = recipientName, TicketNumber = ticketNumber });
        var byPhone = await SendTemplateAsync(
            f, Template(f, null, connectionId: f.ConnectionId, waId: "573009998877") with { RecipientName = recipientName, TicketNumber = ticketNumber });

        Assert.Equal(new SupportReplySendResult.Rejected("recipient_invalid"), toConversation);
        Assert.Equal(new SupportReplySendResult.Rejected("recipient_invalid"), byPhone);
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations"));
        Assert.Equal(0L, await OutboundRowsAsync(f));
        Assert.Empty(Sends(f));
    }

    // La respuesta cabe sola como parámetro (≤ 1024), pero el cuerpo armado con el texto fijo pasa de 1024: Meta la rechazaría.
    [Fact]
    public async Task AReplyThatOverflowsTheBodyWithTheBaseTextIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await SeedApprovedTemplateAsync(f);
        var reply = string.Join(' ', Enumerable.Repeat("respuesta", 110))[..1000].Trim();
        Assert.True(TemplateParameterRules.IsValid(reply));

        var result = await SendTemplateAsync(f, Template(f, f.ConversationId, text: reply));

        Assert.Equal(new SupportReplySendResult.Rejected("template_text_invalid"), result);
        Assert.Equal(0L, await OutboundRowsAsync(f));
        Assert.Empty(Sends(f));
    }

    [Fact]
    public async Task AThreadTextOutsideTheAdvisorRuleIsTextInvalid()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var control = await SendThreadAsync(f, Thread(f, text: "Hola\u0000"));
        var blank = await SendThreadAsync(f, Thread(f, text: "   "));
        var tooLong = await SendThreadAsync(f, Thread(f, text: new string('a', SendMessageValidator.TextMaxLength + 1)));

        Assert.All(new[] { control, blank, tooLong }, result => Assert.Equal(new SupportReplySendResult.Rejected("text_invalid"), result));
        Assert.Equal(0L, await OutboundRowsAsync(f));
        Assert.Empty(Sends(f));
    }

    [Fact]
    public async Task MetaRejectingTheTemplateLeavesAFailedRowAndReturnsFailed()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await SeedApprovedTemplateAsync(f);
        f.Factory.MetaHandler.Reset();
        f.Factory.MetaHandler.Respond("/111/messages$", HttpStatusCode.BadRequest, FakeMetaGraphHandler.GraphError(132015));

        var failed = Assert.IsType<SupportReplySendResult.Failed>(await SendTemplateAsync(f, Template(f, f.ConversationId)));

        Assert.Equal("send_failed", failed.Code);
        Assert.Equal($"11|4|132015|{f.MemberId}", await ScalarAsync<string>(f.ConnectionString,
            "SELECT kind || '|' || status || '|' || failure_code || '|' || sent_by_member_id FROM messaging.messages WHERE id = @id",
            ("id", failed.MessageId!.Value)));
    }

    // Plan E12: Soporte responde aunque la conversación la tenga otro asesor, y no se la quita.
    [Fact]
    public async Task AReplyToAConversationAssignedToAnotherMemberIsSentAndKeepsTheAssignment()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var other = (await SeedMemberAsync(f.ConnectionString, f.Tenant.TenantId, "Beto", "advisor")).MembershipId;
        await ExecuteAsync(f.ConnectionString,
            "UPDATE messaging.conversations SET assigned_member_id = @other, assigned_at = now() WHERE id = @id", ("other", other), ("id", f.ConversationId));

        var sent = Assert.IsType<SupportReplySendResult.Sent>(await SendThreadAsync(f, Thread(f)));

        Assert.Equal(other, await ScalarAsync<Guid>(f.ConnectionString,
            "SELECT assigned_member_id FROM messaging.conversations WHERE id = @id", ("id", f.ConversationId)));
        Assert.Equal(f.MemberId, await ScalarAsync<Guid>(f.ConnectionString,
            "SELECT sent_by_member_id FROM messaging.messages WHERE id = @id", ("id", sent.MessageId)));
        Assert.Single(Sends(f));
    }

    [Fact]
    public async Task AnInstagramReplyInsideTheWindowGoesThroughTheInstagramSender()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        await SeedInstagramConnectionAsync(factory, tenant.TenantId, "Instagram Ventas", InstagramPayloads.DefaultAccountId);
        factory.MetaHandler.Respond($@"^/v24\.0/{InstagramPayloads.DefaultUserId}\?", HttpStatusCode.OK,
            """{"name":"Laura Pérez","username":"laura.perez","id":"1234567890123456"}""");
        using (var anonymous = factory.CreateClient())
        {
            await PostInstagramWebhookAsync(anonymous, InstagramPayloads.Text("m1", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, "hola"));
            await DrainDeliveriesAsync(factory);
        }

        var sendPath = $"/v24.0/{InstagramPayloads.DefaultAccountId}/messages";
        factory.MetaHandler.Respond($"^{sendPath}$", HttpStatusCode.OK,
            $$"""{"recipient_id":"{{InstagramPayloads.DefaultUserId}}","message_id":"mid.support"}""");
        var conversationId = await ScalarAsync<Guid>(connectionString, "SELECT id FROM messaging.conversations");
        var memberId = await OwnerMembershipIdAsync(connectionString, tenant);
        using var scope = factory.Services.CreateScope();
        var outbound = scope.ServiceProvider.GetRequiredService<ISupportReplyOutbound>();

        var sent = Assert.IsType<SupportReplySendResult.Sent>(await outbound.SendThreadAsync(
            new SupportThreadReplySend(tenant.TenantId, conversationId, Guid.CreateVersion7(), memberId, "Ya revisamos tu caso.", UploadId: null), Ct));
        var tooLong = await outbound.SendThreadAsync(
            new SupportThreadReplySend(
                tenant.TenantId, conversationId, Guid.CreateVersion7(), memberId, new string('a', SendMessageValidator.InstagramTextMaxLength + 1), UploadId: null),
            Ct);

        var call = Assert.Single(factory.MetaHandler.Requests, request => request.Uri!.AbsolutePath == sendPath && request.Method == HttpMethod.Post);
        Assert.Contains(InstagramPayloads.DefaultUserId, call.Body, StringComparison.Ordinal);
        Assert.Contains("Ya revisamos tu caso.", call.Body, StringComparison.Ordinal);
        Assert.Equal($"1|{memberId}", await ScalarAsync<string>(connectionString,
            "SELECT status || '|' || sent_by_member_id FROM messaging.messages WHERE id = @id", ("id", sent.MessageId)));
        // Instagram corta en 1000 caracteres: el texto del asesor que pasa de ahí no sale.
        Assert.Equal(new SupportReplySendResult.Rejected("text_invalid"), tooLong);
    }
}
