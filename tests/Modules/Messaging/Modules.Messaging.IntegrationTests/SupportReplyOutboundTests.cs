using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
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
        // Meta da un wamid por mensaje: repetir el del primer envío choca con IX_messages_connection_wamid.
        f.Factory.MetaHandler.Reset();
        f.Factory.MetaHandler.Respond("/111/messages$", HttpStatusCode.OK,
            """{"messaging_product":"whatsapp","contacts":[{"input":"573001234567","wa_id":"573001234567"}],"messages":[{"id":"wamid.support.template"}]}""");
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

    private sealed record MediaFixture(
        QepApiFactory Factory, WebApplicationFactory<Program> Host, FakeMessagingBucket Bucket, string ConnectionString, RegisteredTenant Tenant,
        Guid ConversationId, Guid MemberId);

    /// <summary>Como <see cref="ArrangeAsync"/>, con el bucket de <c>SendMediaMessageApiTests</c> como staging y R2 a la vez, y Graph
    /// contestando la subida del medio y el envío.</summary>
    private static async Task<MediaFixture> ArrangeMediaAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var bucket = new FakeMessagingBucket();
        var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IMessagingMediaStore>(bucket);
            services.AddSingleton<IMessagingUploadStaging>(bucket);
        }));
        var tenant = await RegisterTenantAsync(host);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        await SeedWhatsAppConnectionAsync(host, tenant.TenantId, "Ventas", "111", "222");
        using (var anonymous = host.CreateClient())
        {
            await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", Phone, "w1", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, "hola"));
            await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", "573005556677", "w2", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, "hola"));
            await DrainDeliveriesAsync(host);
        }

        var conversationId = await ScalarAsync<Guid>(connectionString, "SELECT id FROM messaging.conversations WHERE wa_id = @phone", ("phone", Phone));
        var memberId = await OwnerMembershipIdAsync(connectionString, tenant);
        factory.MetaHandler.Respond("/111/media$", HttpStatusCode.OK, """{"id":"media.support"}""");
        ScriptMetaOk(factory.MetaHandler);
        return new MediaFixture(factory, host, bucket, connectionString, tenant, conversationId, memberId);
    }

    /// <summary>Deja el archivo en el staging de quien lo subió para esa conversación, con la clave que arma la sesión de subida.</summary>
    private static Guid Stage(MediaFixture f, Guid conversationId, string fileName, byte[] bytes, Guid? uploadedBy = null)
    {
        var uploadId = Guid.CreateVersion7();
        f.Bucket.Objects[MessagingMediaKeys.Staging(
            f.Tenant.TenantId, conversationId, uploadedBy ?? f.MemberId, uploadId, Path.GetExtension(fileName))] =
            (bytes, "application/octet-stream");
        return uploadId;
    }

    private static byte[] Pdf() => Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<<>>\nendobj\ntrailer\n<<>>\n%%EOF\n");

    private static async Task<SupportReplySendResult> SendThreadAsync(MediaFixture f, SupportThreadReplySend send)
    {
        using var scope = f.Host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ISupportReplyOutbound>().SendThreadAsync(send, Ct);
    }

    // Tarea 11 (plan E19): el adjunto de la respuesta sale por el camino del adjunto del asesor; el texto de la respuesta va como pie.
    [Fact]
    public async Task AThreadReplyWithAnUploadSendsTheMedia()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeMediaAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        var pdf = Pdf();
        var uploadId = Stage(f, f.ConversationId, "contrato.pdf", pdf);

        var sent = Assert.IsType<SupportReplySendResult.Sent>(await SendThreadAsync(f, new SupportThreadReplySend(
            f.Tenant.TenantId, f.ConversationId, Guid.CreateVersion7(), f.MemberId, "Te adjunto el contrato", uploadId, FileName: "contrato.pdf")));

        Assert.Single(f.Factory.MetaHandler.Requests, request => request.Uri!.AbsolutePath == "/v24.0/111/media");
        var send = Assert.Single(f.Factory.MetaHandler.Requests, request => request.Uri!.AbsolutePath == "/v24.0/111/messages" && !IsReadReceipt(request));
        Assert.Contains("\"filename\":\"contrato.pdf\"", send.Body, StringComparison.Ordinal);
        Assert.Contains("\"caption\":\"Te adjunto el contrato\"", send.Body, StringComparison.Ordinal);
        Assert.Equal($"5|1|{f.MemberId}|Te adjunto el contrato|contrato.pdf|media.support", await ScalarAsync<string>(f.ConnectionString,
            """
            SELECT m.kind || '|' || m.status || '|' || m.sent_by_member_id || '|' || m.caption || '|' || mm.file_name || '|' || mm.meta_media_id
            FROM messaging.messages m JOIN messaging.message_media mm ON mm.message_id = m.id WHERE m.id = @id
            """,
            ("id", sent.MessageId)));
        Assert.Equal(pdf, f.Bucket.Objects[$"messaging/{f.Tenant.TenantId}/{sent.MessageId}"].Bytes);
        Assert.DoesNotContain(f.Bucket.Objects.Keys, key => key.StartsWith("messaging/staging/", StringComparison.Ordinal));
    }

    private static async Task AssertNothingWentOutAsync(MediaFixture f)
    {
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 2"));
        Assert.DoesNotContain(
            f.Factory.MetaHandler.Requests,
            request => request.Uri!.AbsolutePath.StartsWith("/v24.0/111/", StringComparison.Ordinal) && !IsReadReceipt(request));
    }

    // La clave del staging lleva la conversación: un uploadId de otra conversación (o vencido) no existe para esta.
    [Fact]
    public async Task AnUploadOfAnotherConversationIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeMediaAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        var other = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.conversations WHERE wa_id = '573005556677'");
        var uploadId = Stage(f, other, "contrato.pdf", Pdf());

        var result = await SendThreadAsync(f, new SupportThreadReplySend(
            f.Tenant.TenantId, f.ConversationId, Guid.CreateVersion7(), f.MemberId, "Te adjunto el contrato", uploadId, FileName: "contrato.pdf"));

        Assert.Equal(new SupportReplySendResult.Rejected("attachment_unavailable"), result);
        await AssertNothingWentOutAsync(f);
    }

    // La clave también lleva la membresía de quien subió: el archivo que subió otro asesor a la misma conversación no existe para quien
    // responde.
    [Fact]
    public async Task AnUploadOfAnotherMemberIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeMediaAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        var uploadId = Stage(f, f.ConversationId, "contrato.pdf", Pdf(), uploadedBy: Guid.CreateVersion7());

        var result = await SendThreadAsync(f, new SupportThreadReplySend(
            f.Tenant.TenantId, f.ConversationId, Guid.CreateVersion7(), f.MemberId, "Te adjunto el contrato", uploadId, FileName: "contrato.pdf"));

        Assert.Equal(new SupportReplySendResult.Rejected("attachment_unavailable"), result);
        await AssertNothingWentOutAsync(f);
    }

    // Sin nombre no hay extensión con qué rearmar la clave del staging.
    [Fact]
    public async Task AnUploadWithoutItsFileNameIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeMediaAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        var uploadId = Stage(f, f.ConversationId, "contrato.pdf", Pdf());

        var result = await SendThreadAsync(f, new SupportThreadReplySend(
            f.Tenant.TenantId, f.ConversationId, Guid.CreateVersion7(), f.MemberId, "Te adjunto el contrato", uploadId, FileName: null));

        Assert.Equal(new SupportReplySendResult.Rejected("attachment_unavailable"), result);
        await AssertNothingWentOutAsync(f);
    }

    // Ruling del controlador (revisión del bloque D): Instagram no lleva pie en un adjunto, así que la respuesta del asesor se perdería.
    // Se rechaza antes de mirar el staging, sin fila ni llamada.
    [Fact]
    public async Task AnUploadToAnInstagramConversationIsRejected()
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

        var conversationId = await ScalarAsync<Guid>(connectionString, "SELECT id FROM messaging.conversations");
        var memberId = await OwnerMembershipIdAsync(connectionString, tenant);
        using var scope = factory.Services.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISupportReplyOutbound>().SendThreadAsync(
            new SupportThreadReplySend(
                tenant.TenantId, conversationId, Guid.CreateVersion7(), memberId, "Te adjunto el contrato", Guid.CreateVersion7(), FileName: "contrato.pdf"),
            Ct);

        Assert.Equal(new SupportReplySendResult.Rejected("attachment_unavailable"), result);
        Assert.Equal(0L, await CountAsync(connectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 2"));
        Assert.DoesNotContain(
            factory.MetaHandler.Requests,
            request => request.Uri!.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal) && request.Method == HttpMethod.Post);
    }

    // ---- Soporte fase 4b (Tarea 6): el documento de la respuesta formal, la plantilla formal y la encuesta ----

    private static byte[] FormalPdf() => Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj\n<<>>\nendobj\ntrailer\n<<>>\n%%EOF\n");

    private static SupportThreadDocumentSend Document(
        RegisteredTenant tenant, Guid conversationId, Guid memberId, string text = "Te enviamos la respuesta formal.", Guid? clientId = null,
        Guid? documentClientId = null) =>
        new(tenant.TenantId, conversationId, clientId ?? Guid.CreateVersion7(), documentClientId ?? Guid.CreateVersion7(), memberId, text, FormalPdf(),
            "Respuesta-SOP-000127.pdf");

    private static async Task<SupportReplySendResult> WithOutboundAsync(
        IServiceProvider services, Func<ISupportReplyOutbound, Task<SupportReplySendResult>> action)
    {
        using var scope = services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<ISupportReplyOutbound>());
    }

    /// <summary>Una fila aprobada del tipo dado, con sus componentes: lo que dejaría el aprovisionamiento ya aprobado por Meta.</summary>
    private static async Task SeedApprovedAsync(
        IServiceProvider services, Guid tenantId, Guid connectionId, Guid memberId, string name, string componentsJson, MessageTemplateKind kind)
    {
        using var scope = services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        dbContext.MessageTemplates.Add(MessageTemplate.Pending(
            tenantId, connectionId, Guid.CreateVersion7().ToString("N"), name, "es", "UTILITY", TemplateComponentsBuilder.Named,
            MessageTemplateStatuses.Approved, componentsJson, kind, memberId, DateTimeOffset.UtcNow));
        await dbContext.SaveChangesAsync(Ct);
    }

    private static Task SeedApprovedFormalAsync(IServiceProvider services, Guid tenantId, Guid connectionId, Guid memberId) =>
        SeedApprovedAsync(
            services, tenantId, connectionId, memberId, SupportFormalReplyTemplateDefinition.Name,
            TemplateComponentsBuilder.Components(
                SupportFormalReplyTemplateDefinition.Draft(SupportFormalReplyTemplateDefinition.Name, SupportFormalReplyTemplateDefinition.BaseBody),
                "4::handle").ToJsonString(),
            MessageTemplateKind.SupportFormalReply);

    private static Task SeedApprovedSurveyAsync(IServiceProvider services, Guid tenantId, Guid connectionId, Guid memberId) =>
        SeedApprovedAsync(
            services, tenantId, connectionId, memberId, SupportSurveyTemplateDefinition.Name,
            TemplateComponentsBuilder.Components(
                SupportSurveyTemplateDefinition.Draft(SupportSurveyTemplateDefinition.Name, SupportSurveyTemplateDefinition.BaseBody)).ToJsonString(),
            MessageTemplateKind.SupportSurvey);

    private static Task<Guid> ConnectionOfAsync(MediaFixture f) =>
        ScalarAsync<Guid>(f.ConnectionString, "SELECT connection_id FROM messaging.conversations WHERE id = @id", ("id", f.ConversationId));

    private static MetaRequest[] Sends(MediaFixture f) =>
        f.Factory.MetaHandler.Requests
            .Where(request => request.Uri!.AbsolutePath == "/v24.0/111/messages" && !IsReadReceipt(request))
            .ToArray();

    // H10: dentro de la ventana de WhatsApp, un solo mensaje de documento con el texto del canal como pie; el PDF sube a Meta y queda en el
    // R2 de Mensajería con la clave del mensaje.
    [Fact]
    public async Task AFormalDocumentInsideTheWindowGoesAsOneDocumentWithCaption()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeMediaAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;

        var sent = Assert.IsType<SupportReplySendResult.Sent>(await WithOutboundAsync(
            f.Host.Services, outbound => outbound.SendThreadDocumentAsync(Document(f.Tenant, f.ConversationId, f.MemberId), Ct)));

        Assert.Equal(f.ConversationId, sent.ConversationId);
        Assert.Equal(sent.MessageId, sent.DocumentMessageId);
        Assert.Single(f.Factory.MetaHandler.Requests, request => request.Uri!.AbsolutePath == "/v24.0/111/media");
        var send = Assert.Single(Sends(f));
        using var body = JsonDocument.Parse(send.Body!);
        Assert.Equal("document", body.RootElement.GetProperty("type").GetString());
        var document = body.RootElement.GetProperty("document");
        Assert.Equal(("media.support", "Respuesta-SOP-000127.pdf", "Te enviamos la respuesta formal."), (
            document.GetProperty("id").GetString(), document.GetProperty("filename").GetString(), document.GetProperty("caption").GetString()));
        Assert.Equal($"5|1|{f.MemberId}|Te enviamos la respuesta formal.|Respuesta-SOP-000127.pdf|application/pdf|media.support", await ScalarAsync<string>(
            f.ConnectionString,
            """
            SELECT m.kind || '|' || m.status || '|' || m.sent_by_member_id || '|' || m.caption || '|' || mm.file_name || '|' || mm.mime_type
                || '|' || mm.meta_media_id
            FROM messaging.messages m JOIN messaging.message_media mm ON mm.message_id = m.id WHERE m.id = @id
            """,
            ("id", sent.MessageId)));
        Assert.Equal(FormalPdf(), f.Bucket.Objects[$"messaging/{f.Tenant.TenantId}/{sent.MessageId}"].Bytes);
    }

    // WhatsApp corta el pie de un documento en 1024: un texto del canal más largo no sale, sin fila ni llamada.
    [Fact]
    public async Task AFormalDocumentWithALongCaptionIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeMediaAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;

        var result = await WithOutboundAsync(f.Host.Services, outbound => outbound.SendThreadDocumentAsync(
            Document(f.Tenant, f.ConversationId, f.MemberId, text: new string('a', SendMediaMessageValidator.MaxCaptionLength + 1)), Ct));

        Assert.Equal(new SupportReplySendResult.Rejected("text_invalid"), result);
        await AssertNothingWentOutAsync(f);
    }

    // H10: un reintento secuencial con los mismos dos clientId devuelve lo que ya salió, aunque la ventana ya se haya cerrado: una sola
    // subida, un solo envío y una sola fila.
    [Fact]
    public async Task RetryingAFormalDocumentWithTheSameClientIdsSendsOnce()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeMediaAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        var send = Document(f.Tenant, f.ConversationId, f.MemberId);

        var first = Assert.IsType<SupportReplySendResult.Sent>(
            await WithOutboundAsync(f.Host.Services, outbound => outbound.SendThreadDocumentAsync(send, Ct)));
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET last_inbound_at = now() - interval '25 hours'");
        var second = Assert.IsType<SupportReplySendResult.Sent>(
            await WithOutboundAsync(f.Host.Services, outbound => outbound.SendThreadDocumentAsync(send, Ct)));

        Assert.Equal(first.MessageId, second.MessageId);
        Assert.Single(Sends(f));
        Assert.Single(f.Factory.MetaHandler.Requests, request => request.Uri!.AbsolutePath == "/v24.0/111/media");
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 2"));
    }

    // H10: en Instagram el documento no lleva pie, así que sale el texto (ClientId) y después el archivo (DocumentClientId) por una URL
    // firmada de R2. Un reintento con los mismos dos no repite ninguno.
    [Fact]
    public async Task AnInstagramFormalReplySendsTheTextThenTheFile()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var bucket = new FakeMessagingBucket();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IMessagingMediaStore>(bucket);
            services.AddSingleton<IMessagingUploadStaging>(bucket);
        }));
        var tenant = await RegisterTenantAsync(host);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        await SeedInstagramConnectionAsync(host, tenant.TenantId, "Instagram Ventas", InstagramPayloads.DefaultAccountId);
        factory.MetaHandler.Respond($@"^/v24\.0/{InstagramPayloads.DefaultUserId}\?", HttpStatusCode.OK,
            """{"name":"Laura Pérez","username":"laura.perez","id":"1234567890123456"}""");
        using (var anonymous = host.CreateClient())
        {
            await PostInstagramWebhookAsync(anonymous, InstagramPayloads.Text("m1", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, "hola"));
            await DrainDeliveriesAsync(host);
        }

        var sendPath = $"/v24.0/{InstagramPayloads.DefaultAccountId}/messages";
        var answers = 0;
        factory.MetaHandler.RespondWith($"^{sendPath}$", _ => FakeMetaGraphHandler.Json(HttpStatusCode.OK,
            $$"""{"recipient_id":"{{InstagramPayloads.DefaultUserId}}","message_id":"mid.support.{{Interlocked.Increment(ref answers)}}"}"""));
        var conversationId = await ScalarAsync<Guid>(connectionString, "SELECT id FROM messaging.conversations");
        var memberId = await OwnerMembershipIdAsync(connectionString, tenant);
        var send = Document(tenant, conversationId, memberId);

        var sent = Assert.IsType<SupportReplySendResult.Sent>(
            await WithOutboundAsync(host.Services, outbound => outbound.SendThreadDocumentAsync(send, Ct)));
        var retried = Assert.IsType<SupportReplySendResult.Sent>(
            await WithOutboundAsync(host.Services, outbound => outbound.SendThreadDocumentAsync(send, Ct)));

        Assert.Equal(sent, retried);
        Assert.NotNull(sent.DocumentMessageId);
        Assert.NotEqual(sent.MessageId, sent.DocumentMessageId);
        var calls = factory.MetaHandler.Requests.Where(request => request.Uri!.AbsolutePath == sendPath && request.Method == HttpMethod.Post).ToArray();
        Assert.Equal(2, calls.Length);
        Assert.Contains("Te enviamos la respuesta formal.", calls[0].Body, StringComparison.Ordinal);
        Assert.Contains("\"file\"", calls[1].Body, StringComparison.Ordinal);
        Assert.Contains($"https://r2.test/messaging/{tenant.TenantId}/{sent.DocumentMessageId}", calls[1].Body, StringComparison.Ordinal);
        Assert.Equal($"{send.ClientId}|1|{send.DocumentClientId}|1", await ScalarAsync<string>(connectionString,
            """
            SELECT t.client_id || '|' || t.status || '|' || d.client_id || '|' || d.status
            FROM messaging.messages t, messaging.messages d WHERE t.id = @text AND d.id = @document
            """,
            ("text", sent.MessageId), ("document", sent.DocumentMessageId!.Value)));
        Assert.Equal(FormalPdf(), bucket.Objects[$"messaging/{tenant.TenantId}/{sent.DocumentMessageId}"].Bytes);
    }

    // H10: fuera de la ventana, la plantilla «Respuesta formal» con el PDF como encabezado de documento, subido a Meta y guardado en R2.
    [Fact]
    public async Task AFormalTemplateCarriesTheDocumentHeader()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeMediaAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        var connectionId = await ConnectionOfAsync(f);
        await SeedApprovedFormalAsync(f.Host.Services, f.Tenant.TenantId, connectionId, f.MemberId);
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET last_inbound_at = now() - interval '25 hours'");

        var sent = Assert.IsType<SupportReplySendResult.Sent>(await WithOutboundAsync(f.Host.Services, outbound => outbound.SendFormalTemplateAsync(
            new SupportFormalTemplateSend(
                f.Tenant.TenantId, f.ConversationId, null, null, null, "Ana Pérez", Guid.CreateVersion7(), f.MemberId, "SOP-000127",
                "Revisamos tu caso", FormalPdf(), "Respuesta-SOP-000127.pdf"),
            Ct)));

        Assert.Equal(f.ConversationId, sent.ConversationId);
        var send = Assert.Single(Sends(f));
        using var body = JsonDocument.Parse(send.Body!);
        var template = body.RootElement.GetProperty("template");
        Assert.Equal("qep_soporte_formal", template.GetProperty("name").GetString());
        var header = template.GetProperty("components")[0];
        Assert.Equal("header", header.GetProperty("type").GetString());
        var document = header.GetProperty("parameters")[0].GetProperty("document");
        Assert.Equal(("media.support", "Respuesta-SOP-000127.pdf"), (document.GetProperty("id").GetString(), document.GetProperty("filename").GetString()));
        Assert.Equal(
            $"11|1|{f.MemberId}|Hola Ana Pérez, adjuntamos la respuesta formal a tu caso SOP-000127. Revisamos tu caso. Si tienes dudas, responde este mensaje.|application/pdf|Respuesta-SOP-000127.pdf",
            await ScalarAsync<string>(f.ConnectionString,
                """
                SELECT m.kind || '|' || m.status || '|' || m.sent_by_member_id || '|' || m.text || '|' || mm.mime_type || '|' || mm.file_name
                FROM messaging.messages m JOIN messaging.message_media mm ON mm.message_id = m.id WHERE m.id = @id
                """,
                ("id", sent.MessageId)));
        Assert.Equal(FormalPdf(), f.Bucket.Objects[$"messaging/{f.Tenant.TenantId}/{sent.MessageId}"].Bytes);
    }

    [Fact]
    public async Task AFormalTemplateWithoutApprovalIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeMediaAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        // La de «Aviso de respuesta» aprobada no sirve: la formal es otro tipo.
        await SeedApprovedAsync(
            f.Host.Services, f.Tenant.TenantId, await ConnectionOfAsync(f), f.MemberId, SupportReplyTemplateDefinition.Name,
            TemplateComponentsBuilder.Components(
                SupportReplyTemplateDefinition.Draft(SupportReplyTemplateDefinition.Name, SupportReplyTemplateDefinition.BaseBody)).ToJsonString(),
            MessageTemplateKind.SupportReply);

        var result = await WithOutboundAsync(f.Host.Services, outbound => outbound.SendFormalTemplateAsync(
            new SupportFormalTemplateSend(
                f.Tenant.TenantId, f.ConversationId, null, null, null, "Ana Pérez", Guid.CreateVersion7(), f.MemberId, "SOP-000127",
                "Revisamos tu caso", FormalPdf(), "Respuesta-SOP-000127.pdf"),
            Ct));

        Assert.Equal(new SupportReplySendResult.Rejected("template_not_approved"), result);
        await AssertNothingWentOutAsync(f);
        Assert.Empty(f.Bucket.Objects);
    }

    // ---- Revisión del Bloque B: qué deja cada falla de la respuesta formal ----

    /// <summary>Graph contesta la subida del PDF con un 400 que no es de credencial mientras <paramref name="uploadFails"/> sea verdadero,
    /// y el envío como siempre.</summary>
    private static void ScriptMediaUpload(MediaFixture f, Func<bool> uploadFails)
    {
        f.Factory.MetaHandler.Reset();
        f.Factory.MetaHandler.RespondWith("/111/media$", _ => uploadFails()
            ? FakeMetaGraphHandler.Json(HttpStatusCode.BadRequest, FakeMetaGraphHandler.GraphError(100))
            : FakeMetaGraphHandler.Json(HttpStatusCode.OK, """{"id":"media.support"}"""));
        ScriptMetaOk(f.Factory.MetaHandler);
    }

    // La subida del PDF de la plantilla va antes de abrir la conversación y del reclamo: si falla no sale nada, no queda fila ni
    // conversación nueva, y Soporte puede borrar su respuesta y reintentar (Rejected, no Failed).
    [Fact]
    public async Task AFormalTemplateWhoseUploadFailsIsRejectedWithoutARowNorAConversation()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeMediaAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        var connectionId = await ConnectionOfAsync(f);
        await SeedApprovedFormalAsync(f.Host.Services, f.Tenant.TenantId, connectionId, f.MemberId);
        ScriptMediaUpload(f, () => true);
        var conversations = await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations");

        var result = await WithOutboundAsync(f.Host.Services, outbound => outbound.SendFormalTemplateAsync(
            new SupportFormalTemplateSend(
                f.Tenant.TenantId, null, connectionId, "573009998877", null, "Ana Pérez", Guid.CreateVersion7(), f.MemberId, "SOP-000127",
                "Revisamos tu caso.", FormalPdf(), "Respuesta-SOP-000127.pdf"),
            Ct));

        Assert.Equal(new SupportReplySendResult.Rejected("send_failed"), result);
        Assert.Equal(conversations, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations"));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations WHERE wa_id = '573009998877'"));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 2"));
        Assert.Empty(Sends(f));
        Assert.Empty(f.Bucket.Objects);
    }

    // Dentro de la ventana, la subida fallida entra a la tabla del envío: la fila queda Failed, y el reintento con los mismos clientId
    // reutiliza ese mensaje en vez de crear otro.
    [Fact]
    public async Task AFormalDocumentWhoseUploadFailsLeavesAFailedRowThatTheRetryReuses()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeMediaAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        var uploadFails = true;
        ScriptMediaUpload(f, () => uploadFails);
        var send = Document(f.Tenant, f.ConversationId, f.MemberId);

        var failed = Assert.IsType<SupportReplySendResult.Failed>(
            await WithOutboundAsync(f.Host.Services, outbound => outbound.SendThreadDocumentAsync(send, Ct)));
        Assert.Equal("send_failed", failed.Code);
        Assert.NotNull(failed.MessageId);
        Assert.Equal("5|4", await ScalarAsync<string>(
            f.ConnectionString, "SELECT kind || '|' || status FROM messaging.messages WHERE id = @id", ("id", failed.MessageId!.Value)));
        Assert.Empty(Sends(f));

        uploadFails = false;
        var retried = Assert.IsType<SupportReplySendResult.Sent>(
            await WithOutboundAsync(f.Host.Services, outbound => outbound.SendThreadDocumentAsync(send, Ct)));

        Assert.Equal(failed.MessageId, retried.MessageId);
        Assert.Equal(retried.MessageId, retried.DocumentMessageId);
        Assert.Single(Sends(f));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 2"));
        Assert.Equal(FormalPdf(), f.Bucket.Objects[$"messaging/{f.Tenant.TenantId}/{retried.MessageId}"].Bytes);
    }

    // Meta rechaza la plantilla formal: la fila queda Failed. El reintento con el mismo clientId vuelve a subir el PDF y sale sobre esa
    // misma fila.
    [Fact]
    public async Task AFormalTemplateRetriedOverAFailedRowSendsOnThatRow()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeMediaAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        var connectionId = await ConnectionOfAsync(f);
        await SeedApprovedFormalAsync(f.Host.Services, f.Tenant.TenantId, connectionId, f.MemberId);
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET last_inbound_at = now() - interval '25 hours'");
        var metaRejects = true;
        f.Factory.MetaHandler.Reset();
        f.Factory.MetaHandler.Respond("/111/media$", HttpStatusCode.OK, """{"id":"media.support"}""");
        f.Factory.MetaHandler.RespondWith("/111/messages$", _ => metaRejects
            ? FakeMetaGraphHandler.Json(HttpStatusCode.BadRequest, FakeMetaGraphHandler.GraphError(132015))
            : FakeMetaGraphHandler.Json(HttpStatusCode.OK,
                """{"messaging_product":"whatsapp","contacts":[{"input":"573001234567","wa_id":"573001234567"}],"messages":[{"id":"wamid.support"}]}"""));
        var send = new SupportFormalTemplateSend(
            f.Tenant.TenantId, f.ConversationId, null, null, null, "Ana Pérez", Guid.CreateVersion7(), f.MemberId, "SOP-000127",
            "Revisamos tu caso.", FormalPdf(), "Respuesta-SOP-000127.pdf");

        var failed = Assert.IsType<SupportReplySendResult.Failed>(
            await WithOutboundAsync(f.Host.Services, outbound => outbound.SendFormalTemplateAsync(send, Ct)));
        metaRejects = false;
        var retried = Assert.IsType<SupportReplySendResult.Sent>(
            await WithOutboundAsync(f.Host.Services, outbound => outbound.SendFormalTemplateAsync(send, Ct)));

        Assert.Equal("send_failed", failed.Code);
        Assert.Equal(failed.MessageId, retried.MessageId);
        Assert.Equal(2, Sends(f).Length);
        Assert.Equal("11|1", await ScalarAsync<string>(
            f.ConnectionString, "SELECT kind || '|' || status FROM messaging.messages WHERE id = @id", ("id", retried.MessageId)));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 2"));
        Assert.Equal(FormalPdf(), f.Bucket.Objects[$"messaging/{f.Tenant.TenantId}/{retried.MessageId}"].Bytes);
    }

    // En Instagram el texto sale primero: si el archivo falla después, el resultado es Failed (el texto ya salió) y el reintento manda sólo
    // el archivo.
    [Fact]
    public async Task AnInstagramFileFailingAfterTheTextIsFailedAndTheRetrySendsOnlyTheFile()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var bucket = new FakeMessagingBucket();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IMessagingMediaStore>(bucket);
            services.AddSingleton<IMessagingUploadStaging>(bucket);
        }));
        var tenant = await RegisterTenantAsync(host);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        await SeedInstagramConnectionAsync(host, tenant.TenantId, "Instagram Ventas", InstagramPayloads.DefaultAccountId);
        factory.MetaHandler.Respond($@"^/v24\.0/{InstagramPayloads.DefaultUserId}\?", HttpStatusCode.OK,
            """{"name":"Laura Pérez","username":"laura.perez","id":"1234567890123456"}""");
        using (var anonymous = host.CreateClient())
        {
            await PostInstagramWebhookAsync(anonymous, InstagramPayloads.Text("m1", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, "hola"));
            await DrainDeliveriesAsync(host);
        }

        var sendPath = $"/v24.0/{InstagramPayloads.DefaultAccountId}/messages";
        var answers = 0;
        // La segunda llamada (el archivo del primer intento) falla; las demás salen.
        factory.MetaHandler.RespondWith($"^{sendPath}$", _ => Interlocked.Increment(ref answers) == 2
            ? FakeMetaGraphHandler.Json(HttpStatusCode.BadRequest, FakeMetaGraphHandler.GraphError(100))
            : FakeMetaGraphHandler.Json(HttpStatusCode.OK,
                $$"""{"recipient_id":"{{InstagramPayloads.DefaultUserId}}","message_id":"mid.support.{{answers}}"}"""));
        var conversationId = await ScalarAsync<Guid>(connectionString, "SELECT id FROM messaging.conversations");
        var memberId = await OwnerMembershipIdAsync(connectionString, tenant);
        var send = Document(tenant, conversationId, memberId);

        var failed = Assert.IsType<SupportReplySendResult.Failed>(
            await WithOutboundAsync(host.Services, outbound => outbound.SendThreadDocumentAsync(send, Ct)));
        var retried = Assert.IsType<SupportReplySendResult.Sent>(
            await WithOutboundAsync(host.Services, outbound => outbound.SendThreadDocumentAsync(send, Ct)));

        Assert.Equal("send_failed", failed.Code);
        Assert.Equal(failed.MessageId, retried.DocumentMessageId);
        var calls = factory.MetaHandler.Requests.Where(request => request.Uri!.AbsolutePath == sendPath && request.Method == HttpMethod.Post).ToArray();
        Assert.Equal(3, calls.Length);
        Assert.Single(calls, call => call.Body!.Contains("Te enviamos la respuesta formal.", StringComparison.Ordinal));
        Assert.Contains("\"file\"", calls[2].Body, StringComparison.Ordinal);
        Assert.Equal($"{send.ClientId}|1|{send.DocumentClientId}|1", await ScalarAsync<string>(connectionString,
            """
            SELECT t.client_id || '|' || t.status || '|' || d.client_id || '|' || d.status
            FROM messaging.messages t, messaging.messages d WHERE t.id = @text AND d.id = @document
            """,
            ("text", retried.MessageId), ("document", retried.DocumentMessageId!.Value)));
    }

    private static SupportSurveyTemplateSend Survey(Fixture f, Guid? clientId = null) =>
        new(f.Tenant.TenantId, f.ConversationId, clientId ?? Guid.CreateVersion7(), f.MemberId, "Ana Pérez", "SOP-000127");

    private static Task<SupportReplySendResult> SendSurveyAsync(Fixture f, SupportSurveyTemplateSend send) =>
        WithOutboundAsync(f, outbound => outbound.SendSurveyTemplateAsync(send, Ct));

    // H16: la encuesta sale por la plantilla aprobada a la conversación del ticket, con el radicado y sin parámetros en los botones.
    [Fact]
    public async Task ASurveyTemplateGoesToTheConversation()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await SeedApprovedSurveyAsync(f.Factory.Services, f.Tenant.TenantId, f.ConnectionId, f.MemberId);
        var clientId = Guid.CreateVersion7();

        var sent = Assert.IsType<SupportReplySendResult.Sent>(await SendSurveyAsync(f, Survey(f, clientId)));
        var retried = Assert.IsType<SupportReplySendResult.Sent>(await SendSurveyAsync(f, Survey(f, clientId)));

        Assert.Equal(sent, retried);
        Assert.Equal(f.ConversationId, sent.ConversationId);
        var call = Assert.Single(Sends(f));
        using var body = JsonDocument.Parse(call.Body!);
        var template = body.RootElement.GetProperty("template");
        Assert.Equal("qep_soporte_encuesta", template.GetProperty("name").GetString());
        var component = Assert.Single(template.GetProperty("components").EnumerateArray());
        Assert.Equal("body", component.GetProperty("type").GetString());
        Assert.Equal(
            [("nombre_contacto", "Ana Pérez"), ("radicado", "SOP-000127")],
            component.GetProperty("parameters").EnumerateArray()
                .Select(item => (item.GetProperty("parameter_name").GetString()!, item.GetProperty("text").GetString()!)));
        Assert.Equal($"11|1|{f.MemberId}|{clientId}", await ScalarAsync<string>(f.ConnectionString,
            "SELECT kind || '|' || status || '|' || sent_by_member_id || '|' || client_id FROM messaging.messages WHERE id = @id",
            ("id", sent.MessageId)));
    }

    [Fact]
    public async Task ASurveyWithoutApprovalIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await SeedApprovedTemplateAsync(f);

        var result = await SendSurveyAsync(f, Survey(f));

        Assert.Equal(new SupportReplySendResult.Rejected("template_not_approved"), result);
        Assert.Equal(0L, await OutboundRowsAsync(f));
        Assert.Empty(Sends(f));
    }

    [Fact]
    public async Task ASurveyToABlockedContactIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await SeedApprovedSurveyAsync(f.Factory.Services, f.Tenant.TenantId, f.ConnectionId, f.MemberId);
        await ExecuteAsync(f.ConnectionString,
            "UPDATE messaging.conversations SET blocked_at = now(), blocked_by_member_id = @member, blocked_wa_id = @phone, version = version + 1",
            ("member", f.MemberId), ("phone", Phone));

        var result = await SendSurveyAsync(f, Survey(f));

        Assert.Equal(new SupportReplySendResult.Rejected("contact_blocked"), result);
        Assert.Equal(0L, await OutboundRowsAsync(f));
        Assert.Empty(Sends(f));
    }

    // Como toda respuesta de Soporte: no toma la conversación, no la reabre ni resta no leídos (seen: 0).
    [Fact]
    public async Task ASurveyDoesNotTakeNorReopenTheConversation()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await SeedApprovedSurveyAsync(f.Factory.Services, f.Tenant.TenantId, f.ConnectionId, f.MemberId);
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET status = 'Resolved', unread_count = 2");
        var version = await ScalarAsync<long>(f.ConnectionString, "SELECT version FROM messaging.conversations");

        Assert.IsType<SupportReplySendResult.Sent>(await SendSurveyAsync(f, Survey(f)));

        Assert.Equal($"Resolved||2|{version}", await ScalarAsync<string>(f.ConnectionString,
            "SELECT status || '|' || coalesce(assigned_member_id::text, '') || '|' || unread_count || '|' || version FROM messaging.conversations"));
    }
}
