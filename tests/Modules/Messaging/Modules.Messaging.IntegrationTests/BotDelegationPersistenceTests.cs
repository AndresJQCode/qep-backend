using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Persistence;
using Npgsql;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-11-17 (agente IA en chatbots) §5.3 contra el esquema migrado: <c>messaging.bot_delegations</c> guarda la delegación,
/// <c>IX_bot_delegations_live</c> frena dos vivas de una conversación y <c>CK_conversations_delegation</c> sólo admite la delegación con el
/// asistente activo en manos de un agente.</summary>
public sealed class BotDelegationPersistenceTests
{
    private const string Brief = """{"objective":"Resuelve dudas","context":"","firstTurn":"lastMessage","idleMinutes":60}""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static BotDelegation NewLive(Guid tenantId, Guid conversationId, Guid connectionId) => BotDelegation.Start(
        Guid.CreateVersion7(), tenantId, conversationId, connectionId, Guid.CreateVersion7(), Guid.CreateVersion7(), "ai-agent-1",
        Guid.CreateVersion7(), null, Brief, DateTimeOffset.UtcNow);

    [Fact]
    public async Task ADelegatedConversationRoundTripsWithItsDelegation()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();
        var conversationId = await SeedConversationAsync(factory, tenantId, connectionId, "573001112233");
        var chatbotId = Guid.CreateVersion7();
        await ActivateAutomationAsync(connectionString, conversationId, chatbotId);
        var agentId = Guid.CreateVersion7();
        Guid delegationId;

        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
            var conversation = await dbContext.Conversations.SingleAsync(value => value.Id == conversationId, Ct);
            var delegation = NewLive(tenantId, conversationId, connectionId);
            delegationId = delegation.Id;
            dbContext.BotDelegations.Add(delegation);
            conversation.DelegateToAgent(delegationId, agentId, DateTimeOffset.UtcNow);
            await dbContext.SaveChangesAsync(Ct);
        }

        Assert.Equal($"Active|2|{agentId}||{delegationId}", await ScalarAsync<string>(connectionString,
            "SELECT automation_status || '|' || automation_bot_kind || '|' || automation_agent_id || '|' || coalesce(automation_chatbot_id::text, '') || '|' || automation_delegation_id FROM messaging.conversations WHERE id = @id",
            ("id", conversationId)));
        Assert.Equal("Live|Resuelve dudas", await ScalarAsync<string>(connectionString,
            "SELECT status || '|' || (brief->>'objective') FROM messaging.bot_delegations WHERE id = @id", ("id", delegationId)));

        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
            var conversation = await dbContext.Conversations.SingleAsync(value => value.Id == conversationId, Ct);
            var delegation = await dbContext.BotDelegations.SingleAsync(value => value.Id == delegationId, Ct);
            delegation.Return("""{"output":"done","reason":"objective_completed"}""", DateTimeOffset.UtcNow);
            conversation.ReturnFromAgent(delegationId, chatbotId, BotDelegationOutputs.Done, DateTimeOffset.UtcNow);
            await dbContext.SaveChangesAsync(Ct);
        }

        Assert.Equal($"Active|1|{chatbotId}|", await ScalarAsync<string>(connectionString,
            "SELECT automation_status || '|' || automation_bot_kind || '|' || automation_chatbot_id || '|' || coalesce(automation_delegation_id::text, '') FROM messaging.conversations WHERE id = @id",
            ("id", conversationId)));
        Assert.Equal("Returned|done", await ScalarAsync<string>(connectionString,
            "SELECT status || '|' || (result->>'output') FROM messaging.bot_delegations WHERE id = @id", ("id", delegationId)));
    }

    [Fact]
    public async Task TwoLiveDelegationsOfOneConversationAreRejectedByTheIndex()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
            dbContext.BotDelegations.Add(NewLive(tenantId, conversationId, connectionId));
            await dbContext.SaveChangesAsync(Ct);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
            dbContext.BotDelegations.Add(NewLive(tenantId, conversationId, connectionId));

            var error = await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync(Ct));

            var postgres = Assert.IsType<PostgresException>(error.InnerException);
            Assert.Equal("23505", postgres.SqlState);
            Assert.Equal("IX_bot_delegations_live", postgres.ConstraintName);
        }

        // Una terminada no cuenta: la siguiente delegación de la misma conversación entra.
        await ExecuteAsync(connectionString,
            "UPDATE messaging.bot_delegations SET status = 'Interrupted', ended_at = now() WHERE conversation_id = @id", ("id", conversationId));
        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
            dbContext.BotDelegations.Add(NewLive(tenantId, conversationId, connectionId));
            await dbContext.SaveChangesAsync(Ct);
        }

        Assert.Equal(2L, await CountAsync(connectionString,
            "SELECT count(*) FROM messaging.bot_delegations WHERE conversation_id = @id", ("id", conversationId)));
    }

    /// <summary>Revisión de la rama, hallazgo 3: por la unidad de trabajo del módulo, el choque del índice sale como la excepción de dominio
    /// que el motor de chatbots atiende; y delegar cierra antes las vivas de la conversación.</summary>
    [Fact]
    public async Task ASecondLiveDelegationIsTheDomainConflictAndInterruptingLiveOnesLetsTheNextIn()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IBotDelegationRepository>().Add(NewLive(tenantId, conversationId, connectionId));
            await scope.ServiceProvider.GetRequiredService<IMessagingUnitOfWork>().SaveChangesAsync(Ct);
        }

        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IBotDelegationRepository>().Add(NewLive(tenantId, conversationId, connectionId));

            var error = await Assert.ThrowsAsync<MessagingDomainException>(
                () => scope.ServiceProvider.GetRequiredService<IMessagingUnitOfWork>().SaveChangesAsync(Ct));

            Assert.Equal(MessagingErrorCodes.DelegationLiveExists, error.Code);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IMessagingUnitOfWork>();
            var delegations = scope.ServiceProvider.GetRequiredService<IBotDelegationRepository>();
            await using var transaction = await unitOfWork.BeginTransactionAsync(Ct);
            // De otro tenant no toca nada.
            Assert.Equal(0, await delegations.InterruptLiveAsync(Guid.CreateVersion7(), conversationId, DateTimeOffset.UtcNow, Ct));
            Assert.Equal(1, await delegations.InterruptLiveAsync(tenantId, conversationId, DateTimeOffset.UtcNow, Ct));
            delegations.Add(NewLive(tenantId, conversationId, connectionId));
            await unitOfWork.SaveChangesAsync(Ct);
            await transaction.CommitAsync(Ct);
        }

        Assert.Equal("Interrupted,Live", await ScalarAsync<string>(connectionString,
            "SELECT string_agg(status, ',' ORDER BY started_at, status) FROM messaging.bot_delegations WHERE conversation_id = @id", ("id", conversationId)));
    }

    /// <summary>Hallazgo 3: el barrido de huérfanas también avisa a la corrida que espera en el bloque, con una señal ReleaseRun del chatbot en
    /// la misma sentencia. Una delegación sin conversación se cierra igual, sin fila.</summary>
    [Fact]
    public async Task TheOrphanSweepSignalsTheChatbotOfEachInterruptedDelegation()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();
        var conversationId = await SeedConversationAsync(factory, tenantId, connectionId, "573001112255");
        var gone = Guid.CreateVersion7();
        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
            dbContext.BotDelegations.Add(NewLive(tenantId, conversationId, connectionId));
            dbContext.BotDelegations.Add(NewLive(tenantId, gone, connectionId));
            await dbContext.SaveChangesAsync(Ct);
        }

        int swept;
        using (var scope = factory.Services.CreateScope())
        {
            swept = await scope.ServiceProvider.GetRequiredService<Modules.Ai.Application.IAiDelegationSweep>()
                .InterruptOrphansAsync(DateTimeOffset.UtcNow, 10, Ct);
        }

        Assert.Equal(2, swept);
        Assert.Equal(0L, await CountAsync(connectionString, "SELECT count(*) FROM messaging.bot_delegations WHERE status = 'Live'"));
        Assert.Equal("ReleaseRun|1", await ScalarAsync<string>(connectionString,
            "SELECT string_agg(kind || '|' || bot_kind, ',') FROM messaging.automation_inbox WHERE conversation_id = @id AND processed_at IS NULL",
            ("id", conversationId)));
        Assert.Equal(0L, await CountAsync(connectionString,
            "SELECT count(*) FROM messaging.automation_inbox WHERE conversation_id = @id", ("id", gone)));
    }

    [Fact]
    public async Task TheDelegationCheckRejectsADelegationOnAPausedConversation()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var conversationId = await SeedConversationAsync(factory, Guid.CreateVersion7(), Guid.CreateVersion7(), "573001112244");
        await ActivateAutomationAsync(connectionString, conversationId, Guid.CreateVersion7());

        // Con un chatbot (bot_kind = 1) tampoco: la delegación es del agente.
        var onChatbot = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connectionString,
            "UPDATE messaging.conversations SET automation_delegation_id = @delegation WHERE id = @id",
            ("id", conversationId), ("delegation", Guid.CreateVersion7())));
        Assert.Equal("CK_conversations_delegation", onChatbot.ConstraintName);

        await ExecuteAsync(connectionString,
            "UPDATE messaging.conversations SET automation_status = 'Paused', automation_paused_at = now(), automation_pause_reason = 'Manual' WHERE id = @id",
            ("id", conversationId));

        var onPaused = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connectionString,
            "UPDATE messaging.conversations SET automation_delegation_id = @delegation WHERE id = @id",
            ("id", conversationId), ("delegation", Guid.CreateVersion7())));

        Assert.Equal("23514", onPaused.SqlState);
        Assert.Equal("CK_conversations_delegation", onPaused.ConstraintName);
    }

    [Fact]
    public async Task AResultWithoutReturningIsRejectedByTheCheck()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var delegation = NewLive(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
            dbContext.BotDelegations.Add(delegation);
            await dbContext.SaveChangesAsync(Ct);
        }

        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connectionString,
            """UPDATE messaging.bot_delegations SET result = '{"output":"done"}' WHERE id = @id""", ("id", delegation.Id)));

        Assert.Equal("CK_bot_delegations_result", error.ConstraintName);
    }
}
