using BuildingBlocks.Application;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Automations.Application;
using Modules.Automations.Domain;
using Modules.Automations.Domain.Engine;
using Modules.Automations.Infrastructure.Persistence;
using static Modules.Automations.IntegrationTests.AutomationsApiHarness;

namespace Modules.Automations.IntegrationTests;

/// <summary>Spec 2026-10-21 §5.2, D-M12 y D-M13 contra Postgres: <c>FOR UPDATE</c>, lock por conversación, barrido con
/// <c>SKIP LOCKED</c> y la traducción del índice de corrida viva.</summary>
public sealed class RunsPersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 15, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ConversationRun NewRun(Guid tenantId, Guid conversationId, RunStatus status, DateTimeOffset at) =>
        ConversationRun.Start(
            Guid.CreateVersion7(), tenantId, conversationId, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            new RunState(status, null, 0, 0, RunVariables.Empty(), []), at);

    private static async Task SeedAsync(QepApiFactory factory, params ConversationRun[] runs)
    {
        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IConversationRunRepository>();
        foreach (var run in runs)
        {
            await repository.AddAsync(run, Ct);
        }

        await scope.ServiceProvider.GetRequiredService<IAutomationsUnitOfWork>().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task ARunRoundTripsThroughTheRepository()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        var run = NewRun(tenantId, conversationId, RunStatus.Waiting, Now);
        await SeedAsync(factory, run);

        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IConversationRunRepository>();
        var latest = await repository.FindLatestAsync(tenantId, conversationId, Ct);
        var listed = await repository.ListAsync(tenantId, run.ChatbotId, Now.AddHours(-1), Now.AddHours(1), Ct);

        Assert.Equal(run.Id, latest?.Id);
        Assert.Equal(RunStatus.Waiting, latest?.Status);
        Assert.Single(listed);
        Assert.Equal("Waiting", await ScalarAsync<string>(database.GetConnectionString(), "SELECT status FROM automations.conversation_runs"));
    }

    [Fact]
    public async Task FindLiveForUpdateBlocksASecondTransaction()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        await SeedAsync(factory, NewRun(tenantId, conversationId, RunStatus.Waiting, Now));

        using var firstScope = factory.Services.CreateScope();
        using var secondScope = factory.Services.CreateScope();
        var firstUnit = firstScope.ServiceProvider.GetRequiredService<IAutomationsUnitOfWork>();
        var secondUnit = secondScope.ServiceProvider.GetRequiredService<IAutomationsUnitOfWork>();
        await using var first = await firstUnit.BeginTransactionAsync(Ct);
        Assert.NotNull(await firstScope.ServiceProvider.GetRequiredService<IConversationRunRepository>()
            .FindLiveForUpdateAsync(tenantId, conversationId, Ct));

        await using var second = await secondUnit.BeginTransactionAsync(Ct);
        var blocked = secondScope.ServiceProvider.GetRequiredService<IConversationRunRepository>()
            .FindLiveForUpdateAsync(tenantId, conversationId, Ct);

        Assert.NotSame(blocked, await Task.WhenAny(blocked, Task.Delay(500, Ct)));
        await firstUnit.SaveChangesAsync(Ct);
        Assert.NotNull(await blocked.WaitAsync(TimeSpan.FromSeconds(10), Ct));
    }

    /// <summary>Ruling P2-R45: el lock por conversación no espera. Mientras una unidad lo tiene, otra recibe <c>false</c> en el momento;
    /// cuando la primera confirma, se suelta.</summary>
    [Fact]
    public async Task TryLockConversationFailsWhileAnotherUnitHoldsIt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var conversationId = Guid.CreateVersion7();

        using var firstScope = factory.Services.CreateScope();
        using var secondScope = factory.Services.CreateScope();
        using var thirdScope = factory.Services.CreateScope();
        var firstUnit = firstScope.ServiceProvider.GetRequiredService<IAutomationsUnitOfWork>();
        var secondUnit = secondScope.ServiceProvider.GetRequiredService<IAutomationsUnitOfWork>();
        var thirdUnit = thirdScope.ServiceProvider.GetRequiredService<IAutomationsUnitOfWork>();
        await using var first = await firstUnit.BeginTransactionAsync(Ct);
        Assert.True(await firstUnit.TryLockConversationAsync(conversationId, Ct));

        await using (await secondUnit.BeginTransactionAsync(Ct))
        {
            Assert.False(await secondUnit.TryLockConversationAsync(conversationId, Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct));
            // Otra conversación no choca.
            Assert.True(await secondUnit.TryLockConversationAsync(Guid.CreateVersion7(), Ct));
        }

        await firstUnit.SaveChangesAsync(Ct);
        await using var third = await thirdUnit.BeginTransactionAsync(Ct);
        Assert.True(await thirdUnit.TryLockConversationAsync(conversationId, Ct));
    }

    [Fact]
    public async Task TryLockConversationWithoutATransactionThrows()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var scope = factory.Services.CreateScope();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => scope.ServiceProvider.GetRequiredService<IAutomationsUnitOfWork>().TryLockConversationAsync(Guid.CreateVersion7(), Ct));
    }

    [Fact]
    public async Task DisposingTheTransactionWithoutSavingRollsBack()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        await SeedAsync(factory, NewRun(tenantId, conversationId, RunStatus.Waiting, Now));

        using (var scope = factory.Services.CreateScope())
        {
            var unit = scope.ServiceProvider.GetRequiredService<IAutomationsUnitOfWork>();
            await using (await unit.BeginTransactionAsync(Ct))
            {
                var live = await scope.ServiceProvider.GetRequiredService<IConversationRunRepository>()
                    .FindLiveForUpdateAsync(tenantId, conversationId, Ct);
                live!.Finish(RunStatus.Interrupted, null, Now.AddMinutes(1));
                await scope.ServiceProvider.GetRequiredService<AutomationsDbContext>().SaveChangesAsync(Ct);
            }
        }

        Assert.Equal("Waiting", await ScalarAsync<string>(database.GetConnectionString(), "SELECT status FROM automations.conversation_runs"));
    }

    [Fact]
    public async Task ClaimAbandonedTakesOnlyWaitingOlderThanTheCutoff()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        var old = NewRun(tenantId, Guid.CreateVersion7(), RunStatus.Waiting, Now.AddHours(-25));
        await SeedAsync(
            factory,
            old,
            NewRun(tenantId, Guid.CreateVersion7(), RunStatus.Waiting, Now.AddHours(-1)),
            NewRun(tenantId, Guid.CreateVersion7(), RunStatus.Running, Now.AddHours(-25)));

        using var scope = factory.Services.CreateScope();
        await using var transaction = await scope.ServiceProvider.GetRequiredService<IAutomationsUnitOfWork>().BeginTransactionAsync(Ct);
        var claimed = await scope.ServiceProvider.GetRequiredService<IConversationRunRepository>()
            .ClaimAbandonedAsync(Now.AddHours(-24), 10, Ct);

        Assert.Equal([old.Id], claimed.Select(run => run.Id));
    }

    [Fact]
    public async Task ClaimAbandonedWithoutATransactionThrows()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var scope = factory.Services.CreateScope();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => scope.ServiceProvider.GetRequiredService<IConversationRunRepository>().ClaimAbandonedAsync(Now, 10, Ct));
    }

    [Fact]
    public async Task ASecondLiveRunIsRejectedByTheIndex()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        await SeedAsync(factory, NewRun(tenantId, conversationId, RunStatus.Waiting, Now));

        var error = await Assert.ThrowsAsync<ResourceConflictException>(
            () => SeedAsync(factory, NewRun(tenantId, conversationId, RunStatus.Running, Now)));

        Assert.Equal(AutomationsErrorCodes.RunAlreadyLive, error.Code);
    }

    [Fact]
    public async Task FindLiveForUpdateWithoutATransactionThrows()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var scope = factory.Services.CreateScope();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => scope.ServiceProvider.GetRequiredService<IConversationRunRepository>()
                .FindLiveForUpdateAsync(Guid.CreateVersion7(), Guid.CreateVersion7(), Ct));
    }

    [Fact]
    public async Task ClaimAbandonedRejectsANonPositiveLimit()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var scope = factory.Services.CreateScope();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => scope.ServiceProvider.GetRequiredService<IConversationRunRepository>().ClaimAbandonedAsync(Now, 0, Ct));
    }

    [Fact]
    public async Task VariablesAndTraceSurviveTheDatabase()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        var trace = new[] { new TraceEntry(Now, "ask", "openQuestion", TraceOutcomes.Waiting) };
        var variables = RunVariables.Parse("""{"nombre":"José Ñandú","edad":42,"pedido":{"id":7,"items":["a","b"]}}""");
        var run = ConversationRun.Start(
            Guid.CreateVersion7(), tenantId, conversationId, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            new RunState(RunStatus.Waiting, "ask", 3, 1, variables, trace), Now);
        await SeedAsync(factory, run);

        using var scope = factory.Services.CreateScope();
        var loaded = await scope.ServiceProvider.GetRequiredService<IConversationRunRepository>()
            .FindLatestAsync(tenantId, conversationId, Ct);

        var state = loaded!.ToState();
        Assert.Equal("José Ñandú", state.Variables.TextOf("nombre"));
        Assert.Equal("42", state.Variables.TextOf("edad"));
        Assert.Equal("7", state.Variables.TextOf("pedido.id"));
        Assert.Equal(trace, state.Trace);
        Assert.Equal(3, state.StepNumber);
        Assert.Equal(1, state.Attempts);
    }

    [Fact]
    public async Task ASecondLiveRunInsideATransactionIsRejectedByTheIndex()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        await SeedAsync(factory, NewRun(tenantId, conversationId, RunStatus.Waiting, Now));

        using var scope = factory.Services.CreateScope();
        var unit = scope.ServiceProvider.GetRequiredService<IAutomationsUnitOfWork>();
        await using var transaction = await unit.BeginTransactionAsync(Ct);
        await scope.ServiceProvider.GetRequiredService<IConversationRunRepository>()
            .AddAsync(NewRun(tenantId, conversationId, RunStatus.Running, Now), Ct);

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() => unit.SaveChangesAsync(Ct));

        Assert.Equal(AutomationsErrorCodes.RunAlreadyLive, error.Code);
    }

    // READ COMMITTED vuelve a evaluar el WHERE de la fila bloqueada cuando el primero commitea: si la dejó terminada, el segundo no la ve.
    [Fact]
    public async Task AfterTheFirstTransactionFinishesTheRunTheSecondFindsNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        await SeedAsync(factory, NewRun(tenantId, conversationId, RunStatus.Waiting, Now));

        using var firstScope = factory.Services.CreateScope();
        using var secondScope = factory.Services.CreateScope();
        var firstUnit = firstScope.ServiceProvider.GetRequiredService<IAutomationsUnitOfWork>();
        await using var first = await firstUnit.BeginTransactionAsync(Ct);
        var live = await firstScope.ServiceProvider.GetRequiredService<IConversationRunRepository>()
            .FindLiveForUpdateAsync(tenantId, conversationId, Ct);

        await using var second = await secondScope.ServiceProvider.GetRequiredService<IAutomationsUnitOfWork>().BeginTransactionAsync(Ct);
        var waiting = secondScope.ServiceProvider.GetRequiredService<IConversationRunRepository>()
            .FindLiveForUpdateAsync(tenantId, conversationId, Ct);
        Assert.NotSame(waiting, await Task.WhenAny(waiting, Task.Delay(500, Ct)));

        live!.Finish(RunStatus.Interrupted, null, Now.AddMinutes(1));
        await firstUnit.SaveChangesAsync(Ct);

        Assert.Null(await waiting.WaitAsync(TimeSpan.FromSeconds(10), Ct));
    }

    /// <summary>
    /// Ruling P2-R46 (D-M13): el barrido abandona cada candidata en su unidad; una que falla (acá, Mensajería no puede devolver su
    /// conversación a None) se registra y sigue esperando para el barrido siguiente, y las demás se abandonan igual.
    /// </summary>
    [Fact]
    public async Task OneFailingAbandonCandidateDoesNotStopTheOthers()
    {
        await using var database = await StartDatabaseAsync();
        var conversations = new FailingEndConversations();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAutomationConversations>();
            services.AddSingleton<IAutomationConversations>(conversations);
        }));
        var tenantId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        // La que falla es la más vieja: el barrido la toma primero.
        var failing = NewRun(tenantId, Guid.CreateVersion7(), RunStatus.Waiting, now.AddHours(-27));
        var other = NewRun(tenantId, Guid.CreateVersion7(), RunStatus.Waiting, now.AddHours(-26));
        conversations.FailFor = failing.ConversationId;
        using (var scope = host.Services.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IConversationRunRepository>();
            await repository.AddAsync(failing, Ct);
            await repository.AddAsync(other, Ct);
            await scope.ServiceProvider.GetRequiredService<IAutomationsUnitOfWork>().SaveChangesAsync(Ct);
        }

        await SweepAutomationsAsync(host, now);

        Assert.Equal("Waiting,Abandoned", await ScalarAsync<string>(database.GetConnectionString(),
            "SELECT string_agg(status, ',' ORDER BY started_at) FROM automations.conversation_runs"));
        Assert.Equal([failing.ConversationId, other.ConversationId], conversations.Ended);
    }

    /// <summary>Las conversaciones de Mensajería en memoria, todas con el bot atendiendo; <see cref="EndAsync"/> falla para una.</summary>
    private sealed class FailingEndConversations : IAutomationConversations
    {
        private readonly List<Guid> ended = [];

        public Guid FailFor { get; set; }

        public IReadOnlyList<Guid> Ended
        {
            get
            {
                lock (ended)
                {
                    return [.. ended];
                }
            }
        }

        public Task<AutomationConversationState?> GetAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
            Task.FromResult<AutomationConversationState?>(new AutomationConversationState(
                "Open", null, "Active", Guid.CreateVersion7(), Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddDays(-2), "Laura", "573001234567"));

        public Task EndAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken)
        {
            lock (ended)
            {
                ended.Add(conversationId);
            }

            return conversationId == FailFor
                ? Task.FromException(new InvalidOperationException("Mensajería no respondió."))
                : Task.CompletedTask;
        }

        public Task StartAsync(Guid tenantId, Guid conversationId, Guid chatbotId, string? firstStepTitle, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SetStepAsync(Guid tenantId, Guid conversationId, string? stepTitle, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SetChatbotAsync(Guid tenantId, Guid conversationId, Guid chatbotId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task HandoffAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ResolveAsync(Guid tenantId, Guid conversationId, Guid? reasonId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
