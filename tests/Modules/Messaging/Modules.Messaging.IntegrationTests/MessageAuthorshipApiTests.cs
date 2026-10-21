using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
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
}
