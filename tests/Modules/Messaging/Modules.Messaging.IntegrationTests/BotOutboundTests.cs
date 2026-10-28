using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-21 D-I4: el bot envía por el mismo camino que un asesor, sin asignar la conversación ni pasar por permisos.</summary>
public sealed class BotOutboundTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConversationId, Guid ChatbotId);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        using (var anonymous = factory.CreateClient())
        {
            await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", "573001234567", "w1", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, "hola"));
            await DrainDeliveriesAsync(factory);
        }

        var conversationId = await ScalarAsync<Guid>(connectionString, "SELECT id FROM messaging.conversations");
        var chatbotId = Guid.CreateVersion7();
        await ActivateAutomationAsync(connectionString, conversationId, chatbotId);
        factory.MetaHandler.Respond("/111/messages", HttpStatusCode.OK,
            """{"messaging_product":"whatsapp","contacts":[{"input":"573001234567","wa_id":"573001234567"}],"messages":[{"id":"wamid.bot"}]}""");
        return new Fixture(factory, connectionString, tenant, conversationId, chatbotId);
    }

    private static int SendCalls(Fixture f) =>
        f.Factory.MetaHandler.Requests.Count(r => r.Uri!.AbsolutePath.EndsWith("/111/messages", StringComparison.Ordinal) && !IsReadReceipt(r));

    private static async Task<BotSendResult> SendAsync(Fixture f, string? text, InteractiveSend? interactive = null, Guid? clientId = null)
    {
        using var scope = f.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IBotOutbound>()
            .SendAsync(new BotSend(f.Tenant.TenantId, f.ConversationId, f.ChatbotId, clientId ?? Guid.CreateVersion7(), text, interactive), Ct);
    }

    private static ButtonsSend Buttons(int count) =>
        new("¿Qué necesitas?", Enumerable.Range(1, count).Select(i => new InteractiveButton($"b{i}", $"Opción {i}")).ToList());

    [Fact]
    public async Task SendsTextAsTheChatbotWithoutAssigning()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var result = await SendAsync(f, "Hola, soy el asistente");

        var sent = Assert.IsType<BotSendResult.Sent>(result);
        Assert.False(sent.AlreadySent);
        Assert.Equal($"2|{f.ChatbotId}|", await ScalarAsync<string>(f.ConnectionString,
            "SELECT sent_by_kind || '|' || sent_by_chatbot_id || '|' || coalesce(sent_by_member_id::text, '') FROM messaging.messages WHERE id = @id", ("id", sent.MessageId)));
        Assert.Equal("|Active", await ScalarAsync<string>(f.ConnectionString,
            "SELECT coalesce(assigned_member_id::text, '') || '|' || automation_status FROM messaging.conversations"));
        Assert.Equal(1, SendCalls(f));
    }

    [Fact]
    public async Task SendsButtonsAndStoresTheInteractiveDetails()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var result = await SendAsync(f, null, Buttons(2));

        var sent = Assert.IsType<BotSendResult.Sent>(result);
        Assert.Equal("10|button|¿Qué necesitas?", await ScalarAsync<string>(f.ConnectionString,
            "SELECT kind || '|' || (details->>'type') || '|' || text FROM messaging.messages WHERE id = @id", ("id", sent.MessageId)));
        var post = Assert.Single(f.Factory.MetaHandler.Requests, r => r.Uri!.AbsolutePath.EndsWith("/111/messages", StringComparison.Ordinal) && !IsReadReceipt(r));
        Assert.Contains("\"type\":\"interactive\"", post.Body, StringComparison.Ordinal);
        // D-I2: el hilo trae los botones armados para pintarlos (regla BFF), no el objeto de Meta.
        using var client = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ReadPermissions);
        var page = await client.GetFromJsonAsync<JsonElement>(MessagesUrl(f.Tenant.TenantId, f.ConversationId), Ct);
        var message = page.GetProperty("items").EnumerateArray().Single(m => m.GetProperty("id").GetGuid() == sent.MessageId);
        var interactive = message.GetProperty("interactive");
        Assert.Equal("button", interactive.GetProperty("type").GetString());
        Assert.Equal("b1", interactive.GetProperty("buttons")[0].GetProperty("id").GetString());
        Assert.Equal("Opción 2", interactive.GetProperty("buttons")[1].GetProperty("title").GetString());
    }

    [Fact]
    public Task ANullCharacterIsRejectedWithoutARow() => AssertRejectedWithoutARowAsync("Hola\u0000");

    [Fact]
    public Task ATextOverTheAdvisorLimitIsRejectedWithoutARow() => AssertRejectedWithoutARowAsync(new string('a', SendMessageValidator.TextMaxLength + 1));

    /// <summary>M2: lo que a un asesor se le rechaza (SendMessageValidator) tampoco sale como el bot, y no deja fila.</summary>
    private static async Task AssertRejectedWithoutARowAsync(string text)
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var result = await SendAsync(f, text);

        Assert.Equal("messaging.message.rejected", Assert.IsType<BotSendResult.Rejected>(result).Code);
        Assert.Equal(0, SendCalls(f));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 2"));
    }

    [Fact]
    public async Task AControlCharacterInsideTheInteractiveIsRejectedWithoutARow()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var result = await SendAsync(f, null, new ButtonsSend("¿Qué necesitas?", [new InteractiveButton("b1", "Uno\u0007")]));

        Assert.Equal("messaging.message.rejected", Assert.IsType<BotSendResult.Rejected>(result).Code);
        Assert.Equal(0, SendCalls(f));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 2"));
    }

    [Fact]
    public async Task RepeatedClientIdReturnsTheExistingMessage()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var clientId = Guid.CreateVersion7();

        var first = Assert.IsType<BotSendResult.Sent>(await SendAsync(f, "Hola", clientId: clientId));
        var second = Assert.IsType<BotSendResult.Sent>(await SendAsync(f, "Hola", clientId: clientId));

        Assert.False(first.AlreadySent);
        Assert.True(second.AlreadySent);
        Assert.Equal(first.MessageId, second.MessageId);
        Assert.Equal(1, SendCalls(f));
    }

    [Fact]
    public async Task RefusesWhenAnAdvisorHoldsTheConversation()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var client = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ManagePermissions);
        var version = await ScalarAsync<long>(f.ConnectionString, "SELECT version FROM messaging.conversations");
        var take = await MessagingApiHarness.SendAsync(client, HttpMethod.Post, $"{ConversationUrl(f.Tenant.TenantId, f.ConversationId)}/take", null, $"\"{version}\"");
        Assert.Equal(HttpStatusCode.OK, take.StatusCode);

        var result = await SendAsync(f, "Hola");

        Assert.IsType<BotSendResult.NotAutomated>(result);
        Assert.Equal(0, SendCalls(f));
    }

    [Fact]
    public async Task RefusesWhenTheWindowIsClosed()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET last_inbound_at = now() - interval '25 hours'");

        var result = await SendAsync(f, "Hola");

        Assert.IsType<BotSendResult.WindowClosed>(result);
        Assert.Equal(0, SendCalls(f));
        // La ventana se mira antes de reclamar: no queda fila saliente.
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 2"));
    }

    [Fact]
    public async Task AnInvalidInteractiveIsRejectedBeforeMeta()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var result = await SendAsync(f, null, Buttons(4));

        Assert.Equal("messaging.interactive.invalid", Assert.IsType<BotSendResult.Rejected>(result).Code);
        Assert.Equal(0, SendCalls(f));
    }

    [Fact]
    public async Task AGraphRejectionBecomesRejected()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        f.Factory.MetaHandler.Reset();
        f.Factory.MetaHandler.Respond("/111/messages", HttpStatusCode.BadRequest, FakeMetaGraphHandler.GraphError(131026));

        var result = await SendAsync(f, "Hola");

        Assert.Equal("messaging.message.rejected", Assert.IsType<BotSendResult.Rejected>(result).Code);
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 2 AND status = 4"));
    }

    [Fact]
    public async Task ATakeBetweenTwoSendsInTheSameScopeIsSeen()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var scope = f.Factory.Services.CreateScope();
        var bot = scope.ServiceProvider.GetRequiredService<IBotOutbound>();
        BotSend Next() => new(f.Tenant.TenantId, f.ConversationId, f.ChatbotId, Guid.CreateVersion7(), "Hola", null);

        Assert.IsType<BotSendResult.Sent>(await bot.SendAsync(Next(), Ct));
        using var client = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ManagePermissions);
        var version = await ScalarAsync<long>(f.ConnectionString, "SELECT version FROM messaging.conversations");
        var take = await MessagingApiHarness.SendAsync(client, HttpMethod.Post, $"{ConversationUrl(f.Tenant.TenantId, f.ConversationId)}/take", null, $"\"{version}\"");
        Assert.Equal(HttpStatusCode.OK, take.StatusCode);

        Assert.IsType<BotSendResult.NotAutomated>(await bot.SendAsync(Next(), Ct));
        Assert.Equal(1, SendCalls(f));
    }

    // Spec 2026-10-26 §3.4: un contacto bloqueado no recibe nada del bot; la guarda va antes del reclamo, así que no deja fila.
    [Fact]
    public async Task ABlockedConversationIsNotSentAndLeavesNoRow()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var owner = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM tenancy.memberships LIMIT 1");
        await ExecuteAsync(f.ConnectionString,
            $"UPDATE messaging.conversations SET blocked_at = now(), blocked_by_member_id = '{owner}', blocked_wa_id = '573001234567', version = version + 1");
        var rowsBefore = await ScalarAsync<long>(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 2");

        Assert.IsType<BotSendResult.Blocked>(await SendAsync(f, "Hola"));

        Assert.Equal(0, SendCalls(f));
        Assert.Equal(rowsBefore, await ScalarAsync<long>(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 2"));
    }

    [Theory]
    [InlineData("UPDATE messaging.conversations SET automation_status = 'Paused', automation_paused_at = now(), automation_pause_reason = 'Manual'")]
    [InlineData("UPDATE messaging.conversations SET status = 'Resolved', automation_status = 'None', automation_started_at = NULL, automation_chatbot_id = NULL")]
    public async Task RefusesWhenTheAutomationIsPausedOrTheConversationResolved(string sql)
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await ExecuteAsync(f.ConnectionString, sql);

        Assert.IsType<BotSendResult.NotAutomated>(await SendAsync(f, "Hola"));
        Assert.Equal(0, SendCalls(f));
    }

    // Spec 2026-10-24 D-T11: el motivo y el nombre de la conexión viajan con el resultado, para el hilo del traspaso.
    [Fact]
    public async Task APausedConnectionIsConnectionUnavailablePaused()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await ExecuteAsync(f.ConnectionString, "UPDATE integrations.connections SET status = 'Paused'");

        Assert.Equal(new BotSendResult.ConnectionUnavailable("paused", "Ventas"), await SendAsync(f, "Hola"));
        Assert.Equal(0, SendCalls(f));
    }

    /// <summary>Spec 2026-10-24, caso 1 del owner: el token no descifra (base restaurada con otra llave). Sin llamar a Meta y sin fila.</summary>
    [Fact]
    public async Task AnUnreadableTokenIsConnectionUnavailableWithItsReason()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await ExecuteAsync(
            f.ConnectionString,
            "UPDATE integrations.connection_secrets SET ciphertext = set_byte(ciphertext, 20, get_byte(ciphertext, 20) # 255)");

        Assert.Equal(new BotSendResult.ConnectionUnavailable("secret_unreadable", "Ventas"), await SendAsync(f, "Hola"));
        Assert.Equal(0, SendCalls(f));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 2"));
    }

    [Fact]
    public async Task ATokenRejectedByMetaIsCredentialsRejected()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        f.Factory.MetaHandler.Reset();
        f.Factory.MetaHandler.Respond("/111/messages", HttpStatusCode.Unauthorized, FakeMetaGraphHandler.GraphError(190));

        Assert.Equal(new BotSendResult.ConnectionUnavailable("credentials_rejected", "Ventas"), await SendAsync(f, "Hola"));
        Assert.Equal("NeedsAttention", await ScalarAsync<string>(f.ConnectionString, "SELECT status FROM integrations.connections"));
    }

    [Fact]
    public async Task MetaClosingTheWindowBecomesWindowClosed()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        f.Factory.MetaHandler.Reset();
        f.Factory.MetaHandler.Respond("/111/messages", HttpStatusCode.BadRequest, FakeMetaGraphHandler.GraphError(131047));

        Assert.IsType<BotSendResult.WindowClosed>(await SendAsync(f, "Hola"));
    }

    [Fact]
    public async Task AFailedRowWithTheSameClientIdIsResent()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var clientId = Guid.CreateVersion7();
        f.Factory.MetaHandler.Reset();
        f.Factory.MetaHandler.Respond("/111/messages", HttpStatusCode.BadRequest, FakeMetaGraphHandler.GraphError(131026));
        Assert.IsType<BotSendResult.Rejected>(await SendAsync(f, "Hola", clientId: clientId));
        f.Factory.MetaHandler.Reset();
        f.Factory.MetaHandler.Respond("/111/messages", HttpStatusCode.OK,
            """{"messaging_product":"whatsapp","contacts":[{"input":"573001234567","wa_id":"573001234567"}],"messages":[{"id":"wamid.retry"}]}""");

        var second = Assert.IsType<BotSendResult.Sent>(await SendAsync(f, "Hola", clientId: clientId));

        Assert.False(second.AlreadySent);
        Assert.Equal(2, SendCalls(f));
        Assert.Equal("1|1", await ScalarAsync<string>(f.ConnectionString,
            "SELECT status || '|' || (SELECT count(*) FROM messaging.messages WHERE direction = 2) FROM messaging.messages WHERE id = @id", ("id", second.MessageId)));
    }

    [Fact]
    public async Task ThePostCarriesTheCallbackWithTheMessageId()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var sent = Assert.IsType<BotSendResult.Sent>(await SendAsync(f, "Hola"));

        var post = Assert.Single(f.Factory.MetaHandler.Requests, r => r.Uri!.AbsolutePath.EndsWith("/111/messages", StringComparison.Ordinal) && !IsReadReceipt(r));
        Assert.Contains($"\"biz_opaque_callback_data\":\"qep:{sent.MessageId}\"", post.Body, StringComparison.Ordinal);
    }
}
