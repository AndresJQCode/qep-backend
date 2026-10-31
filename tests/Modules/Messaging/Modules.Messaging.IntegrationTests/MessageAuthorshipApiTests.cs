using System.Net.Http.Json;
using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using Npgsql;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-21 D-I3 y §5.5: el reclamo escribe <c>sent_by_kind = 2</c> exactamente cuando viene el chatbot, y el
/// hilo lo devuelve como <c>sentByChatbot</c> (sin <c>sentBy</c>).</summary>
public sealed class MessageAuthorshipApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheThreadShowsTheChatbotAuthor()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        var conversationId = await SeedConversationAsync(factory, tenant.TenantId, connectionId, "573001234567");
        var chatbotId = Guid.CreateVersion7();
        await ExecuteAsync(
            connectionString,
            """
            INSERT INTO messaging.messages (id, conversation_id, tenant_id, connection_id, occurred_at, direction, kind, status, text, created_at, sent_by_kind, sent_by_chatbot_id, sent_by_member_id)
            VALUES (gen_random_uuid(), @conversationId, @tenantId, @connectionId, now(), 2, 1, 1, 'Hola desde el bot', now(), 2, @chatbotId, NULL)
            """,
            ("conversationId", conversationId), ("tenantId", tenant.TenantId), ("connectionId", connectionId), ("chatbotId", chatbotId));
        var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ReadPermissions);

        var page = await client.GetFromJsonAsync<JsonElement>(MessagesUrl(tenant.TenantId, conversationId), Ct);

        var message = page.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(JsonValueKind.Null, message.GetProperty("sentBy").ValueKind);
        Assert.Equal("Chatbot", message.GetProperty("sentByChatbot").GetProperty("name").GetString());
        Assert.Equal(chatbotId, message.GetProperty("sentByChatbot").GetProperty("chatbotId").GetGuid());
    }

    /// <summary>Task 16: con el adaptador real de Automations el hilo muestra el nombre guardado del chatbot, no el «Chatbot» del
    /// respaldo; uno borrado (id desconocido) sigue saliendo como «Chatbot».</summary>
    [Fact]
    public async Task TheThreadShowsTheRealChatbotName()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        var conversationId = await SeedConversationAsync(factory, tenant.TenantId, connectionId, "573001234567");
        var chatbotId = Guid.CreateVersion7();
        var deletedChatbotId = Guid.CreateVersion7();
        await ExecuteAsync(
            connectionString,
            """
            INSERT INTO automations.chatbots (id, tenant_id, name, draft_flow, draft_updated_at, draft_updated_by, created_at, created_by, updated_at)
            VALUES (@chatbotId, @tenantId, 'Servicio técnico', '{"schema":1,"start":{"firstNodeId":null},"variables":[],"nodes":[]}'::jsonb, now(), @by, now(), @by, now())
            """,
            ("chatbotId", chatbotId), ("tenantId", tenant.TenantId), ("by", tenant.OwnerUserId));
        foreach (var (id, text, minutes) in new[] { (chatbotId, "Hola desde el bot", 1), (deletedChatbotId, "Hola desde uno borrado", 2) })
        {
            await ExecuteAsync(
                connectionString,
                """
                INSERT INTO messaging.messages (id, conversation_id, tenant_id, connection_id, occurred_at, direction, kind, status, text, created_at, sent_by_kind, sent_by_chatbot_id, sent_by_member_id)
                VALUES (gen_random_uuid(), @conversationId, @tenantId, @connectionId, now() + make_interval(mins => @minutes), 2, 1, 1, @text, now(), 2, @chatbotId, NULL)
                """,
                ("conversationId", conversationId), ("tenantId", tenant.TenantId), ("connectionId", connectionId), ("chatbotId", id),
                ("text", text), ("minutes", minutes));
        }

        var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ReadPermissions);

        var page = await client.GetFromJsonAsync<JsonElement>(MessagesUrl(tenant.TenantId, conversationId), Ct);

        var names = page.GetProperty("items").EnumerateArray()
            .ToDictionary(item => item.GetProperty("text").GetString()!, item => item.GetProperty("sentByChatbot").GetProperty("name").GetString());
        Assert.Equal("Servicio técnico", names["Hola desde el bot"]);
        Assert.Equal("Chatbot", names["Hola desde uno borrado"]);
    }

    [Fact]
    public async Task AClaimWithAChatbotWritesKindTwoAndNoMember()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        var conversationId = await SeedConversationAsync(factory, tenant.TenantId, connectionId, "573001234567");
        var chatbotId = Guid.CreateVersion7();
        var at = DateTimeOffset.UtcNow;
        var draft = new OutboundDraft(
            Guid.CreateVersion7(), conversationId, tenant.TenantId, connectionId, Guid.CreateVersion7(), "hola", null, at, SentByChatbotId: chatbotId);

        using (var scope = factory.Services.CreateScope())
        {
            await using var claim = await scope.ServiceProvider.GetRequiredService<IOutboundMessages>().ClaimAsync(draft, Ct);
            await claim.CommitSentAsync("wamid.bot", at, null, Ct);
        }

        Assert.Equal($"2|{chatbotId}|", await ScalarAsync<string>(connectionString,
            "SELECT sent_by_kind || '|' || sent_by_chatbot_id || '|' || coalesce(sent_by_member_id::text, '') FROM messaging.messages WHERE id = @id", ("id", draft.MessageId)));
    }

    [Fact]
    public async Task AFailedMemberRowRetriedByAChatbotEndsAsKindTwoWithoutMember()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        var conversationId = await SeedConversationAsync(factory, tenant.TenantId, connectionId, "573001234567");
        var memberId = await OwnerMembershipIdAsync(connectionString, tenant);
        var chatbotId = Guid.CreateVersion7();
        var clientId = Guid.CreateVersion7();
        var at = DateTimeOffset.UtcNow;
        var first = new OutboundDraft(Guid.CreateVersion7(), conversationId, tenant.TenantId, connectionId, clientId, "hola", memberId, at);
        var retry = new OutboundDraft(Guid.CreateVersion7(), conversationId, tenant.TenantId, connectionId, clientId, "hola", null, at, SentByChatbotId: chatbotId);

        using (var scope = factory.Services.CreateScope())
        {
            await using var claim = await scope.ServiceProvider.GetRequiredService<IOutboundMessages>().ClaimAsync(first, Ct);
            await claim.CommitFailedAsync(131026, "x", at, null, Ct);
        }

        using (var scope = factory.Services.CreateScope())
        {
            await using var claim = await scope.ServiceProvider.GetRequiredService<IOutboundMessages>().ClaimAsync(retry, Ct);
            Assert.False(claim.Inserted);
            await claim.CommitSentAsync("wamid.retry", at, null, Ct);
        }

        Assert.Equal($"2|{chatbotId}||1", await ScalarAsync<string>(connectionString,
            "SELECT sent_by_kind || '|' || sent_by_chatbot_id || '|' || coalesce(sent_by_member_id::text, '') || '|' || status FROM messaging.messages WHERE id = @id", ("id", first.MessageId)));
    }

    private const string RawApiInsert =
        """
        INSERT INTO messaging.messages (id, conversation_id, tenant_id, connection_id, occurred_at, direction, kind, status, text, created_at,
                                        sent_by_kind, sent_by_member_id, sent_by_api_key_id, sent_by_api_key_name)
        VALUES (gen_random_uuid(), @conversationId, @tenantId, @connectionId, now(), 2, 1, 1, 'Hola', now(), @kind, @member, @keyId, @keyName)
        """;

    private static async Task<PostgresException> RawInsertFailsAsync(short kind, Guid? member, Guid? keyId, string? keyName)
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        var conversationId = await SeedConversationAsync(factory, tenant.TenantId, connectionId, "573001234567");
        return await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            connectionString, RawApiInsert,
            ("conversationId", conversationId), ("tenantId", tenant.TenantId), ("connectionId", connectionId), ("kind", kind),
            ("member", (object?)member ?? DBNull.Value), ("keyId", (object?)keyId ?? DBNull.Value), ("keyName", (object?)keyName ?? DBNull.Value)));
    }

    [Fact]
    public async Task AKindFourRowWithAMemberFailsInTheDatabase()
    {
        var error = await RawInsertFailsAsync(4, Guid.CreateVersion7(), Guid.CreateVersion7(), "Integración");

        Assert.Equal("23514", error.SqlState);
        Assert.Equal("CK_messages_sent_by_one", error.ConstraintName);
    }

    [Fact]
    public async Task AKindFourRowWithoutItsKeyFailsInTheDatabase()
    {
        var error = await RawInsertFailsAsync(4, null, null, null);

        Assert.Equal("23514", error.SqlState);
        Assert.Equal("CK_messages_sent_by_api_key", error.ConstraintName);
    }

    [Fact]
    public async Task AKindOneRowWithAKeyFailsInTheDatabase()
    {
        var error = await RawInsertFailsAsync(1, null, Guid.CreateVersion7(), "Integración");

        Assert.Equal("23514", error.SqlState);
        Assert.Equal("CK_messages_sent_by_api_key", error.ConstraintName);
    }

    [Fact]
    public async Task TheThreadSignsAnApiMessageWithTheKeyName()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        var conversationId = await SeedConversationAsync(factory, tenant.TenantId, connectionId, "573001234567");
        var keyId = Guid.CreateVersion7();
        await ExecuteAsync(
            connectionString, RawApiInsert,
            ("conversationId", conversationId), ("tenantId", tenant.TenantId), ("connectionId", connectionId), ("kind", (short)4),
            ("member", DBNull.Value), ("keyId", keyId), ("keyName", "Integración ERP"));
        var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ReadPermissions);

        var page = await client.GetFromJsonAsync<JsonElement>(MessagesUrl(tenant.TenantId, conversationId), Ct);

        var message = page.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(JsonValueKind.Null, message.GetProperty("sentBy").ValueKind);
        Assert.Equal(JsonValueKind.Null, message.GetProperty("sentByChatbot").ValueKind);
        Assert.Equal(keyId, message.GetProperty("sentByApiKey").GetProperty("id").GetGuid());
        Assert.Equal("Integración ERP", message.GetProperty("sentByApiKey").GetProperty("name").GetString());
    }

    [Fact]
    public async Task ADraftNeedsExactlyOneAuthor()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        var conversationId = await SeedConversationAsync(factory, tenant.TenantId, connectionId, "573001234567");
        var draft = new OutboundDraft(
            Guid.CreateVersion7(), conversationId, tenant.TenantId, connectionId, Guid.CreateVersion7(), "hola", Guid.CreateVersion7(), DateTimeOffset.UtcNow,
            SentByApiKey: new OutboundApiKeyAuthor(Guid.CreateVersion7(), "ERP"));

        using var scope = factory.Services.CreateScope();
        await Assert.ThrowsAsync<ArgumentException>(() => scope.ServiceProvider.GetRequiredService<IOutboundMessages>().ClaimAsync(draft, Ct));

        Assert.Equal(0L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM messaging.messages WHERE tenant_id = @t", ("t", tenant.TenantId)));
    }

    [Fact]
    public async Task ARepeatedClientIdInAnotherConversationOfTheTenantFails()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        var first = await SeedConversationAsync(factory, tenant.TenantId, connectionId, "573001234567");
        var second = await SeedConversationAsync(factory, tenant.TenantId, connectionId, "573001234568");
        var clientId = Guid.CreateVersion7();
        var at = DateTimeOffset.UtcNow;
        var key = new OutboundApiKeyAuthor(Guid.CreateVersion7(), "ERP");

        using (var scope = factory.Services.CreateScope())
        {
            await using var claim = await scope.ServiceProvider.GetRequiredService<IOutboundMessages>().ClaimAsync(
                new OutboundDraft(Guid.CreateVersion7(), first, tenant.TenantId, connectionId, clientId, "hola", null, at, SentByApiKey: key), Ct);
            await claim.CommitSentAsync("wamid.api1", at, null, Ct);
        }

        using var other = factory.Services.CreateScope();
        var error = await Assert.ThrowsAsync<ResourceConflictException>(() => other.ServiceProvider.GetRequiredService<IOutboundMessages>().ClaimAsync(
            new OutboundDraft(Guid.CreateVersion7(), second, tenant.TenantId, connectionId, clientId, "hola", null, at, SentByApiKey: key), Ct));

        Assert.Equal("external.client_id_reused", error.Code);
    }

    [Fact]
    public async Task TheSameClientIdInAnotherTenantIsAllowed()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var clientId = Guid.CreateVersion7();
        var at = DateTimeOffset.UtcNow;
        var key = new OutboundApiKeyAuthor(Guid.CreateVersion7(), "ERP");

        foreach (var phone in new[] { "573001234567", "573001234568" })
        {
            var tenant = await RegisterTenantAsync(factory);
            var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111" + phone[^1], "222" + phone[^1]);
            var conversationId = await SeedConversationAsync(factory, tenant.TenantId, connectionId, phone);
            using var scope = factory.Services.CreateScope();
            await using var claim = await scope.ServiceProvider.GetRequiredService<IOutboundMessages>().ClaimAsync(
                new OutboundDraft(Guid.CreateVersion7(), conversationId, tenant.TenantId, connectionId, clientId, "hola", null, at, SentByApiKey: key), Ct);
            Assert.True(claim.Inserted);
            await claim.CommitSentAsync("wamid." + phone, at, null, Ct);
        }

        Assert.Equal(2L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM messaging.messages WHERE client_id = @c AND sent_by_kind = 4", ("c", clientId)));
    }
}
