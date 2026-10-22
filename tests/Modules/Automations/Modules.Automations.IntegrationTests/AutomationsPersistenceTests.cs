using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Modules.Automations.Application;
using Modules.Automations.Domain;
using static Modules.Automations.IntegrationTests.AutomationsApiHarness;

namespace Modules.Automations.IntegrationTests;

/// <summary>Spec 2026-10-21 §4.6 y tarea 13: repositorio, unidad de trabajo (traducción por nombre de índice), consultas de
/// corridas y recorder de auditoría contra Postgres, resueltos del host como los usarán los handlers.</summary>
public sealed class AutomationsPersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 15, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Chatbot NewChatbot(Guid tenantId, string name) =>
        Chatbot.Create(tenantId, name, string.Empty, ChatbotTrigger.NewConversation, [], "{}", "{}", Guid.CreateVersion7(), Now);

    [Fact]
    public async Task ADuplicatedNameBecomesNameTaken()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IChatbotRepository>();
        await repository.AddAsync(NewChatbot(tenantId, "Ventas"), Ct);
        await repository.AddAsync(NewChatbot(tenantId, "ventas"), Ct);

        var error = await Assert.ThrowsAsync<ResourceConflictException>(
            () => scope.ServiceProvider.GetRequiredService<IAutomationsUnitOfWork>().SaveChangesAsync(Ct));

        Assert.Equal(AutomationsErrorCodes.ChatbotNameTaken, error.Code);
    }

    [Fact]
    public async Task TwoPublishedChatbotsOnOneConnectionBecomeConnectionTaken()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();
        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IChatbotRepository>();
        foreach (var name in new[] { "Ventas", "Soporte" })
        {
            var chatbot = NewChatbot(tenantId, name);
            chatbot.SetConnections([connectionId], Now);
            chatbot.Publish([], Guid.CreateVersion7(), Now);
            await repository.AddAsync(chatbot, Ct);
        }

        var error = await Assert.ThrowsAsync<ResourceConflictException>(
            () => scope.ServiceProvider.GetRequiredService<IAutomationsUnitOfWork>().SaveChangesAsync(Ct));

        Assert.Equal(AutomationsErrorCodes.ChatbotConnectionTaken, error.Code);
    }

    [Fact]
    public async Task AStaleVersionBecomesConcurrencyConflict()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        var chatbot = NewChatbot(tenantId, "Ventas");
        using (var seed = factory.Services.CreateScope())
        {
            await seed.ServiceProvider.GetRequiredService<IChatbotRepository>().AddAsync(chatbot, Ct);
            await seed.ServiceProvider.GetRequiredService<IAutomationsUnitOfWork>().SaveChangesAsync(Ct);
        }

        // Dos lectores del mismo chatbot: el segundo guarda con la versión que el primero ya dejó atrás.
        using var first = factory.Services.CreateScope();
        using var second = factory.Services.CreateScope();
        var firstCopy = await first.ServiceProvider.GetRequiredService<IChatbotRepository>().FindAsync(tenantId, chatbot.Id, Ct);
        var secondCopy = await second.ServiceProvider.GetRequiredService<IChatbotRepository>().FindAsync(tenantId, chatbot.Id, Ct);
        Assert.NotNull(firstCopy);
        Assert.NotNull(secondCopy);
        firstCopy.Rename("Ventas 2", string.Empty, Now);
        await first.ServiceProvider.GetRequiredService<IAutomationsUnitOfWork>().SaveChangesAsync(Ct);
        secondCopy.Rename("Ventas 3", string.Empty, Now);

        var error = await Assert.ThrowsAsync<RequestConcurrencyException>(
            () => second.ServiceProvider.GetRequiredService<IAutomationsUnitOfWork>().SaveChangesAsync(Ct));

        Assert.Equal("concurrency.conflict", error.Code);
    }

    [Fact]
    public async Task FindPublishedByConnectionIgnoresDrafts()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();
        var draft = NewChatbot(tenantId, "Borrador");
        draft.SetConnections([connectionId], Now);
        using (var seed = factory.Services.CreateScope())
        {
            await seed.ServiceProvider.GetRequiredService<IChatbotRepository>().AddAsync(draft, Ct);
            await seed.ServiceProvider.GetRequiredService<IAutomationsUnitOfWork>().SaveChangesAsync(Ct);
        }

        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IChatbotRepository>();
        Assert.Null(await repository.FindPublishedByConnectionAsync(tenantId, connectionId, Ct));

        var tracked = await repository.FindAsync(tenantId, draft.Id, Ct);
        Assert.NotNull(tracked);
        tracked.Publish([], Guid.CreateVersion7(), Now);
        await scope.ServiceProvider.GetRequiredService<IAutomationsUnitOfWork>().SaveChangesAsync(Ct);

        using var reader = factory.Services.CreateScope();
        var published = await reader.ServiceProvider.GetRequiredService<IChatbotRepository>()
            .FindPublishedByConnectionAsync(tenantId, connectionId, Ct);
        Assert.NotNull(published);
        Assert.Equal(draft.Id, published.Id);
        Assert.Single(published.Versions);
        Assert.Null(await reader.ServiceProvider.GetRequiredService<IChatbotRepository>()
            .FindPublishedByConnectionAsync(Guid.CreateVersion7(), connectionId, Ct));
    }

    [Fact]
    public async Task TodayCountsRunsStartedTodayOnly()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var chatbotId = Guid.CreateVersion7();
        var dayStart = new DateTimeOffset(2026, 10, 21, 5, 0, 0, TimeSpan.Zero);
        await InsertRunAsync(connectionString, tenantId, chatbotId, "Completed", dayStart.AddHours(1));
        await InsertRunAsync(connectionString, tenantId, chatbotId, "HandedOff", dayStart.AddHours(2));
        await InsertRunAsync(connectionString, tenantId, chatbotId, "HandedOff", dayStart.AddHours(-3));

        using var scope = factory.Services.CreateScope();
        var queries = scope.ServiceProvider.GetRequiredService<IConversationRunQueries>();
        var today = await queries.TodayAsync(tenantId, [chatbotId, Guid.CreateVersion7()], dayStart, dayStart.AddDays(1), Ct);

        Assert.Equal((2, 1), today[chatbotId]);
        Assert.Single(today);
        Assert.False(await queries.HasLiveRunsAsync(tenantId, chatbotId, Ct));
        await InsertRunAsync(connectionString, tenantId, chatbotId, "Waiting", dayStart.AddHours(3));
        Assert.True(await queries.HasLiveRunsAsync(tenantId, chatbotId, Ct));
    }

    [Fact]
    public async Task RecordWritesAnAuditEntryWithTheResourceType()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var chatbot = NewChatbot(tenantId, "Ventas");
        var actorId = Guid.CreateVersion7();
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IChatbotRepository>().AddAsync(chatbot, Ct);
            scope.ServiceProvider.GetRequiredService<IAutomationsAuditRecorder>().Record(
                tenantId, actorId, false, AutomationsAuditActions.ChatbotCreated, AutomationsAuditActions.ResourceChatbot, chatbot.Id, Now);
            await scope.ServiceProvider.GetRequiredService<IAutomationsUnitOfWork>().SaveChangesAsync(Ct);
        }

        var count = await CountAsync(
            connectionString,
            "SELECT count(*) FROM audit.entries WHERE tenant_id = @t AND action = 'automations.chatbot.created' AND resource_type = 'chatbot' AND resource_id = @r",
            ("t", tenantId), ("r", chatbot.Id.ToString()));
        Assert.Equal(1L, count);
    }

    private static Task<int> InsertRunAsync(string connectionString, Guid tenantId, Guid chatbotId, string status, DateTimeOffset startedAt) =>
        ExecuteAsync(connectionString,
            """
            INSERT INTO automations.conversation_runs (id, tenant_id, conversation_id, connection_id, chatbot_id, chatbot_version_id, status, started_at, updated_at)
            VALUES (@id, @tenantId, @conversationId, @connectionId, @chatbotId, @versionId, @status, @startedAt, @startedAt)
            """,
            ("id", Guid.CreateVersion7()), ("tenantId", tenantId), ("conversationId", Guid.CreateVersion7()),
            ("connectionId", Guid.CreateVersion7()), ("chatbotId", chatbotId), ("versionId", Guid.CreateVersion7()),
            ("status", status), ("startedAt", startedAt));
}
