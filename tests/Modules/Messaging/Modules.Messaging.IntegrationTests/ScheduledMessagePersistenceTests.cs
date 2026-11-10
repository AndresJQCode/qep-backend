using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Npgsql;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-11-09 «Modelo de datos»: la tabla <c>scheduled_messages</c>, sus índices únicos parciales y CHECKs, la columna
/// <c>messages.retry_disabled</c> y el candado de fila del repositorio, contra Postgres.</summary>
public sealed class ScheduledMessagePersistenceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static readonly Guid MemberId = Guid.CreateVersion7();

    private static async Task<(QepApiFactory Factory, string ConnectionString, Guid TenantId, Guid ConnectionId, Guid ConversationId)> SeedAsync(
        TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();
        var conversationId = await SeedConversationAsync(factory, tenantId, connectionId, "573001234567");
        return (factory, connectionString, tenantId, connectionId, conversationId);
    }

    private static ScheduledMessage NewText(Guid tenantId, Guid conversationId, Guid? clientId = null) =>
        ScheduledMessage.CreateText(
            Guid.CreateVersion7(), tenantId, conversationId, clientId ?? Guid.CreateVersion7(), "hola", Now.AddHours(1), MemberId, Now);

    private static async Task AddAsync(QepApiFactory factory, ScheduledMessage message)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IScheduledMessageRepository>().AddAsync(message, TestContext.Current.CancellationToken);
    }

    private static Task<int> SetStatusAsync(string connectionString, Guid id, string status) =>
        ExecuteAsync(connectionString, "UPDATE messaging.scheduled_messages SET status = @status WHERE id = @id", ("status", status), ("id", id));

    [Fact]
    public async Task ASecondActiveRowInTheConversationIsAlreadyExists()
    {
        await using var database = await StartDatabaseAsync();
        var (factory, _, tenantId, _, conversationId) = await SeedAsync(database);
        using var host = factory;
        await AddAsync(factory, NewText(tenantId, conversationId));

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() => AddAsync(factory, NewText(tenantId, conversationId)));

        Assert.Equal("messaging.scheduled.already_exists", error.Code);
    }

    [Fact]
    public async Task ACancelledRowDoesNotCountAsActive()
    {
        await using var database = await StartDatabaseAsync();
        var (factory, connectionString, tenantId, _, conversationId) = await SeedAsync(database);
        using var host = factory;
        var first = NewText(tenantId, conversationId);
        await AddAsync(factory, first);
        await SetStatusAsync(connectionString, first.Id, "Cancelled");

        await AddAsync(factory, NewText(tenantId, conversationId));

        Assert.Equal(2, await CountAsync(connectionString, "SELECT count(*) FROM messaging.scheduled_messages WHERE conversation_id = @c", ("c", conversationId)));
    }

    [Fact]
    public async Task AFailedRowStillCountsAsActive()
    {
        await using var database = await StartDatabaseAsync();
        var (factory, connectionString, tenantId, _, conversationId) = await SeedAsync(database);
        using var host = factory;
        var first = NewText(tenantId, conversationId);
        await AddAsync(factory, first);
        await SetStatusAsync(connectionString, first.Id, "Failed");

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() => AddAsync(factory, NewText(tenantId, conversationId)));

        Assert.Equal("messaging.scheduled.already_exists", error.Code);
    }

    [Fact]
    public async Task TheSameClientIdTwiceIsAlreadyExists()
    {
        await using var database = await StartDatabaseAsync();
        var (factory, connectionString, tenantId, _, conversationId) = await SeedAsync(database);
        using var host = factory;
        var clientId = Guid.CreateVersion7();
        var first = NewText(tenantId, conversationId, clientId);
        await AddAsync(factory, first);
        // Cancelada: el único activo ya no estorba, así que el choque es sólo por (conversación, clientId).
        await SetStatusAsync(connectionString, first.Id, "Cancelled");

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() => AddAsync(factory, NewText(tenantId, conversationId, clientId)));

        Assert.Equal("messaging.scheduled.already_exists", error.Code);
        using var scope = factory.Services.CreateScope();
        var found = await scope.ServiceProvider.GetRequiredService<IScheduledMessageRepository>()
            .FindByClientIdAsync(tenantId, conversationId, clientId, TestContext.Current.CancellationToken);
        Assert.Equal(first.Id, found?.Id);
    }

    [Fact]
    public async Task TheShapeChecksRejectATextWithMedia()
    {
        await using var database = await StartDatabaseAsync();
        var (factory, connectionString, tenantId, _, conversationId) = await SeedAsync(database);
        using var host = factory;

        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connectionString,
            """
            INSERT INTO messaging.scheduled_messages (id, tenant_id, conversation_id, client_id, kind, is_voice_note, text, media_mime_type, scheduled_at, status,
                                                      author_member_id, created_by_member_id, created_at, attempts, version)
            VALUES (@id, @tenantId, @conversationId, @clientId, 'Text', false, 'hola', 'image/jpeg', now(), 'Scheduled', @member, @member, now(), 0, 1)
            """,
            ("id", Guid.CreateVersion7()), ("tenantId", tenantId), ("conversationId", conversationId), ("clientId", Guid.CreateVersion7()), ("member", MemberId)));

        Assert.Equal("23514", error.SqlState);
    }

    [Fact]
    public async Task RetryDisabledDefaultsToFalseOnExistingMessages()
    {
        await using var database = await StartDatabaseAsync();
        var (factory, connectionString, tenantId, connectionId, conversationId) = await SeedAsync(database);
        using var host = factory;
        var messageId = await SeedOutboundAsync(connectionString, conversationId, tenantId, connectionId, "wamid.1");

        var retryDisabled = await ScalarAsync<bool>(connectionString, "SELECT retry_disabled FROM messaging.messages WHERE id = @id", ("id", messageId));

        Assert.False(retryDisabled);
    }

    [Fact]
    public async Task LockSeesTheRowAndCommitDiscardBurnsTheFailedMessage()
    {
        await using var database = await StartDatabaseAsync();
        var (factory, connectionString, tenantId, connectionId, conversationId) = await SeedAsync(database);
        using var host = factory;
        var scheduled = NewText(tenantId, conversationId);
        await AddAsync(factory, scheduled);
        await SetStatusAsync(connectionString, scheduled.Id, "Failed");
        var failedMessageId = Guid.CreateVersion7();
        var otherMessageId = await SeedOutboundAsync(connectionString, conversationId, tenantId, connectionId, null, status: 4, failureCode: -1);
        await ExecuteAsync(connectionString,
            """
            INSERT INTO messaging.messages (id, conversation_id, tenant_id, connection_id, occurred_at, direction, kind, status, text, client_id, failure_code, created_at)
            VALUES (@id, @conversationId, @tenantId, @connectionId, now(), 2, 1, 4, 'hola', @clientId, -1, now())
            """,
            ("id", failedMessageId), ("conversationId", conversationId), ("tenantId", tenantId), ("connectionId", connectionId), ("clientId", scheduled.ClientId));

        using (var scope = factory.Services.CreateScope())
        {
            await using var rowLock = await scope.ServiceProvider.GetRequiredService<IScheduledMessageRepository>()
                .LockAsync(tenantId, conversationId, scheduled.Id, TestContext.Current.CancellationToken);
            Assert.NotNull(rowLock);
            Assert.Equal(ScheduledMessageStatus.Failed, rowLock.Message.Status);
            rowLock.Message.Discard(MemberId, Now);
            await rowLock.CommitDiscardAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal("Cancelled", await ScalarAsync<string>(connectionString, "SELECT status FROM messaging.scheduled_messages WHERE id = @id", ("id", scheduled.Id)));
        Assert.True(await ScalarAsync<bool>(connectionString, "SELECT retry_disabled FROM messaging.messages WHERE id = @id", ("id", failedMessageId)));
        Assert.False(await ScalarAsync<bool>(connectionString, "SELECT retry_disabled FROM messaging.messages WHERE id = @id", ("id", otherMessageId)));
    }
}
