using Npgsql;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-21 §4.2 contra Postgres: la autoría de chatbot en <c>messages</c>, el chatbot obligatorio de una conversación
/// con asistente y la clave de <c>automation_inbox</c>. Los CHECK se comprueban por nombre de constraint.</summary>
public sealed class AutomationEnginePersistenceTests
{
    private const string InsertMessage =
        """
        INSERT INTO messaging.messages (id, conversation_id, tenant_id, connection_id, occurred_at, direction, kind, status, text, sent_by_kind, sent_by_chatbot_id, sent_by_member_id, created_at)
        VALUES (@id, @conversationId, @tenantId, @connectionId, now(), 2, 1, 1, 'hola', @kind, @chatbotId, @memberId, now())
        """;

    private static async Task<(string ConnectionString, Guid ConversationId, Guid TenantId, Guid ConnectionId)> SeedAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();
        var conversationId = await SeedConversationAsync(factory, tenantId, connectionId, "573001234567");
        return (connectionString, conversationId, tenantId, connectionId);
    }

    private static Task<int> InsertMessageAsync(
        string connectionString, Guid conversationId, Guid tenantId, Guid connectionId, short kind, Guid? chatbotId, Guid? memberId) =>
        ExecuteAsync(connectionString, InsertMessage,
            ("id", Guid.CreateVersion7()), ("conversationId", conversationId), ("tenantId", tenantId), ("connectionId", connectionId),
            ("kind", kind), ("chatbotId", (object?)chatbotId ?? DBNull.Value), ("memberId", (object?)memberId ?? DBNull.Value));

    private static Task<int> InsertInboxAsync(string connectionString, Guid messageId, Guid conversationId, Guid tenantId, Guid connectionId) =>
        ExecuteAsync(connectionString,
            "INSERT INTO messaging.automation_inbox (message_id, tenant_id, connection_id, conversation_id, opened, created_at, next_attempt_at) VALUES (@m, @t, @c, @v, true, now(), now())",
            ("m", messageId), ("t", tenantId), ("c", connectionId), ("v", conversationId));

    [Fact]
    public async Task AChatbotMessageWithoutChatbotIdIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        var (connectionString, conversationId, tenantId, connectionId) = await SeedAsync(database);

        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertMessageAsync(connectionString, conversationId, tenantId, connectionId, kind: 2, chatbotId: null, memberId: null));

        Assert.Equal("CK_messages_sent_by_chatbot", error.ConstraintName);
    }

    [Fact]
    public async Task AChatbotMessageCannotAlsoHaveAMember()
    {
        await using var database = await StartDatabaseAsync();
        var (connectionString, conversationId, tenantId, connectionId) = await SeedAsync(database);

        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertMessageAsync(connectionString, conversationId, tenantId, connectionId, kind: 2, chatbotId: Guid.CreateVersion7(), memberId: Guid.CreateVersion7()));

        Assert.Equal("CK_messages_sent_by_one", error.ConstraintName);
    }

    [Fact]
    public async Task AnActiveConversationNeedsAChatbotId()
    {
        await using var database = await StartDatabaseAsync();
        var (connectionString, conversationId, _, _) = await SeedAsync(database);

        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connectionString,
            "UPDATE messaging.conversations SET automation_status = 'Active', automation_started_at = now(), automation_chatbot_id = NULL WHERE id = @id",
            ("id", conversationId)));

        Assert.Equal("CK_conversations_automation_chatbot", error.ConstraintName);
    }

    [Fact]
    public async Task TheInboxKeysByMessageId()
    {
        await using var database = await StartDatabaseAsync();
        var (connectionString, conversationId, tenantId, connectionId) = await SeedAsync(database);
        var messageId = Guid.CreateVersion7();
        await InsertInboxAsync(connectionString, messageId, conversationId, tenantId, connectionId);

        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertInboxAsync(connectionString, messageId, conversationId, tenantId, connectionId));

        Assert.Equal("23505", error.SqlState);
    }

    [Fact]
    public async Task ExistingMessagesDefaultToMemberKind()
    {
        await using var database = await StartDatabaseAsync();
        var (connectionString, conversationId, tenantId, connectionId) = await SeedAsync(database);
        var messageId = await SeedOutboundAsync(connectionString, conversationId, tenantId, connectionId, "wamid.1");

        Assert.Equal(1, await ScalarAsync<short>(connectionString, "SELECT sent_by_kind FROM messaging.messages WHERE id = @id", ("id", messageId)));
    }
}
