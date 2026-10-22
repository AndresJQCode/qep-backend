using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Automations.Infrastructure.Persistence;
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

    // D-C13: pausar no libera la conexión; el motor decide por Status.
    // D-C13: ocupan la conexión el activo y el pausado (fila activa); el borrador no, ni el de otro tenant.
    [Fact]
    public async Task ListServingConnectionsProjectsThePublishedOnes()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        var (first, second, third) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var active = NewChatbot(tenantId, "Activo");
        active.SetConnections([first], Now);
        active.Publish([], Guid.CreateVersion7(), Now);
        var paused = NewChatbot(tenantId, "Pausado");
        paused.SetConnections([second], Now);
        paused.Publish([], Guid.CreateVersion7(), Now);
        paused.Pause(Now);
        var draft = NewChatbot(tenantId, "Borrador");
        draft.SetConnections([third], Now);
        var foreign = NewChatbot(Guid.CreateVersion7(), "Ajeno");
        foreign.SetConnections([third], Now);
        foreign.Publish([], Guid.CreateVersion7(), Now);
        using (var seed = factory.Services.CreateScope())
        {
            var repository = seed.ServiceProvider.GetRequiredService<IChatbotRepository>();
            foreach (var chatbot in new[] { active, paused, draft, foreign })
            {
                await repository.AddAsync(chatbot, Ct);
            }

            await seed.ServiceProvider.GetRequiredService<IAutomationsUnitOfWork>().SaveChangesAsync(Ct);
        }

        using var scope = factory.Services.CreateScope();
        var serving = await scope.ServiceProvider.GetRequiredService<IChatbotRepository>().ListServingConnectionsAsync(tenantId, Ct);

        Assert.Equal(
            [new ChatbotServingConnection(first, active.Id, "Activo"), new ChatbotServingConnection(second, paused.Id, "Pausado")],
            serving.OrderBy(row => row.Name, StringComparer.Ordinal));
    }

    // La lista proyectada: número e instante de la versión publicada (no de otra), conexiones y cambios del borrador.
    [Fact]
    public async Task ListSummariesProjectsThePublishedVersionAndTheConnections()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();
        var published = NewChatbot(tenantId, "Ventas");
        published.SetConnections([connectionId], Now);
        published.Publish([], Guid.CreateVersion7(), Now);
        published.Publish([], Guid.CreateVersion7(), Now.AddMinutes(5));
        var draft = NewChatbot(tenantId, "Agenda");
        using (var seed = factory.Services.CreateScope())
        {
            var repository = seed.ServiceProvider.GetRequiredService<IChatbotRepository>();
            await repository.AddAsync(published, Ct);
            await repository.AddAsync(draft, Ct);
            await seed.ServiceProvider.GetRequiredService<IAutomationsUnitOfWork>().SaveChangesAsync(Ct);
        }

        using var scope = factory.Services.CreateScope();
        var repositoryUnderTest = scope.ServiceProvider.GetRequiredService<IChatbotRepository>();
        var rows = await repositoryUnderTest.ListSummariesAsync(tenantId, null, Ct);
        var searched = await repositoryUnderTest.ListSummariesAsync(tenantId, "ven", Ct);

        Assert.Equal(["Agenda", "Ventas"], rows.Select(row => row.Name));
        var agenda = rows[0];
        Assert.Equal(((int?)null, (DateTimeOffset?)null, true, ChatbotStatus.Draft), (agenda.PublishedNumber, agenda.PublishedAt, agenda.HasDraftChanges, agenda.Status));
        var ventas = Assert.Single(searched);
        Assert.Equal(((int?)2, (DateTimeOffset?)Now.AddMinutes(5), false, ChatbotStatus.Active), (ventas.PublishedNumber, ventas.PublishedAt, ventas.HasDraftChanges, ventas.Status));
        Assert.Equal([connectionId], ventas.ConnectionIds);
        Assert.Equal(published.Version, ventas.Version);
    }

    [Fact]
    public async Task FindPublishedByConnectionReturnsAPausedChatbot()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();
        var chatbot = NewChatbot(tenantId, "Ventas");
        chatbot.SetConnections([connectionId], Now);
        chatbot.Publish([], Guid.CreateVersion7(), Now);
        chatbot.Pause(Now);
        using (var seed = factory.Services.CreateScope())
        {
            await seed.ServiceProvider.GetRequiredService<IChatbotRepository>().AddAsync(chatbot, Ct);
            await seed.ServiceProvider.GetRequiredService<IAutomationsUnitOfWork>().SaveChangesAsync(Ct);
        }

        using var scope = factory.Services.CreateScope();
        var found = await scope.ServiceProvider.GetRequiredService<IChatbotRepository>()
            .FindPublishedByConnectionAsync(tenantId, connectionId, Ct);

        Assert.NotNull(found);
        Assert.Equal(ChatbotStatus.Paused, found.Status);
    }

    [Fact]
    public async Task ListSearchesCaseInsensitivelyTreatsWildcardsAsLiteralsAndScopesByTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        using (var seed = factory.Services.CreateScope())
        {
            var repository = seed.ServiceProvider.GetRequiredService<IChatbotRepository>();
            foreach (var name in new[] { "Ventas Bogotá", "Soporte_100", "Soporte X100", "Oferta 50%", "Oferta 500" })
            {
                await repository.AddAsync(NewChatbot(tenantId, name), Ct);
            }

            await repository.AddAsync(NewChatbot(Guid.CreateVersion7(), "Ventas Otro Tenant"), Ct);
            await seed.ServiceProvider.GetRequiredService<IAutomationsUnitOfWork>().SaveChangesAsync(Ct);
        }

        using var scope = factory.Services.CreateScope();
        var chatbots = scope.ServiceProvider.GetRequiredService<IChatbotRepository>();

        Assert.Equal(["Ventas Bogotá"], (await chatbots.ListSummariesAsync(tenantId, "vENTAS", Ct)).Select(row => row.Name));
        Assert.Equal(["Soporte_100"], (await chatbots.ListSummariesAsync(tenantId, "_100", Ct)).Select(row => row.Name));
        Assert.Equal(["Oferta 50%"], (await chatbots.ListSummariesAsync(tenantId, "50%", Ct)).Select(row => row.Name));
        // Un «%» suelto no es comodín: sólo casa el nombre que lo trae.
        Assert.Equal(["Oferta 50%"], (await chatbots.ListSummariesAsync(tenantId, "%", Ct)).Select(row => row.Name));
        Assert.Equal(
            ["Oferta 50%", "Oferta 500", "Soporte X100", "Soporte_100", "Ventas Bogotá"],
            (await chatbots.ListSummariesAsync(tenantId, null, Ct)).Select(row => row.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task TodayIncludesTheStartBoundExcludesTheEndBoundAndOtherTenants()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var chatbotId = Guid.CreateVersion7();
        var dayStart = new DateTimeOffset(2026, 10, 21, 5, 0, 0, TimeSpan.Zero);
        var dayEnd = dayStart.AddDays(1);
        await InsertRunAsync(connectionString, tenantId, chatbotId, "Completed", dayStart);
        await InsertRunAsync(connectionString, tenantId, chatbotId, "HandedOff", dayEnd);
        await InsertRunAsync(connectionString, Guid.CreateVersion7(), chatbotId, "Completed", dayStart.AddHours(1));

        using var scope = factory.Services.CreateScope();
        // El mismo instante con offset de Bogotá: se normaliza a UTC antes de llegar a Npgsql.
        var today = await scope.ServiceProvider.GetRequiredService<IConversationRunQueries>()
            .TodayAsync(tenantId, [chatbotId], dayStart.ToOffset(TimeSpan.FromHours(-5)), dayEnd.ToOffset(TimeSpan.FromHours(-5)), Ct);

        Assert.Equal((1, 0), today[chatbotId]);
    }

    [Fact]
    public async Task ADuplicatedTemplateNameBecomesTemplateNameTaken()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        await InsertTemplateAsync(connectionString, tenantId, "Menu");
        var other = await InsertTemplateAsync(connectionString, tenantId, "Otro");

        using var scope = factory.Services.CreateScope();
        var template = await scope.ServiceProvider.GetRequiredService<IChatbotTemplateRepository>().FindAsync(tenantId, other, Ct);
        Assert.NotNull(template);
        scope.ServiceProvider.GetRequiredService<AutomationsDbContext>().Entry(template).Property(value => value.Name).CurrentValue = "MENU";

        var error = await Assert.ThrowsAsync<ResourceConflictException>(
            () => scope.ServiceProvider.GetRequiredService<IAutomationsUnitOfWork>().SaveChangesAsync(Ct));

        Assert.Equal(AutomationsErrorCodes.TemplateNameTaken, error.Code);
    }

    // Dos publicaciones que proponen el mismo número: la segunda choca con el índice de versiones, no con el token del chatbot.
    [Fact]
    public async Task ARacedVersionNumberBecomesConcurrencyConflict()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var chatbot = NewChatbot(tenantId, "Ventas");
        using (var seed = factory.Services.CreateScope())
        {
            await seed.ServiceProvider.GetRequiredService<IChatbotRepository>().AddAsync(chatbot, Ct);
            await seed.ServiceProvider.GetRequiredService<IAutomationsUnitOfWork>().SaveChangesAsync(Ct);
        }

        using var scope = factory.Services.CreateScope();
        var loaded = await scope.ServiceProvider.GetRequiredService<IChatbotRepository>().FindAsync(tenantId, chatbot.Id, Ct);
        Assert.NotNull(loaded);
        await ExecuteAsync(connectionString,
            """
            INSERT INTO automations.chatbot_versions (id, chatbot_id, tenant_id, number, flow, layout, published_at, published_by)
            VALUES (@id, @chatbotId, @tenantId, 1, '{}'::jsonb, '{}'::jsonb, now(), @by)
            """,
            ("id", Guid.CreateVersion7()), ("chatbotId", chatbot.Id), ("tenantId", tenantId), ("by", Guid.CreateVersion7()));
        loaded.Publish([], Guid.CreateVersion7(), Now);

        var error = await Assert.ThrowsAsync<RequestConcurrencyException>(
            () => scope.ServiceProvider.GetRequiredService<IAutomationsUnitOfWork>().SaveChangesAsync(Ct));

        Assert.Equal("concurrency.conflict", error.Code);
    }

    private static async Task<Guid> InsertTemplateAsync(string connectionString, Guid tenantId, string name)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(connectionString,
            """
            INSERT INTO automations.chatbot_templates (id, tenant_id, name, category, flow, layout, created_at, created_by)
            VALUES (@id, @tenantId, @name, 'general', '{}'::jsonb, '{}'::jsonb, now(), @by)
            """,
            ("id", id), ("tenantId", tenantId), ("name", name), ("by", Guid.CreateVersion7()));
        return id;
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
