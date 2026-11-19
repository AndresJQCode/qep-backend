using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Modules.Pipeline.Application;
using Modules.Pipeline.Domain;
using Modules.Pipeline.Infrastructure.Persistence;
using static Modules.Pipeline.IntegrationTests.PipelineApiHarness;

namespace Modules.Pipeline.IntegrationTests;

/// <summary>
/// Chatbot CRM action spec (2026-11-19) «Which deal» against the database: <see cref="IChatbotDealMover"/> moves the open deal
/// that came from this conversation if it is the conversation customer's, else that customer's only open deal; never a guess,
/// never another customer's deal. It saves in its own unit of work as the system (audit source <c>chatbot</c>), and on a version
/// conflict re-reads and tries once more.
/// </summary>
public sealed class ChatbotDealMoverTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Fixture(TestDatabase database, QepApiFactory factory, RegisteredTenant tenant, Guid owner) : IAsyncDisposable
    {
        public string ConnectionString { get; } = database.GetConnectionString();

        public QepApiFactory Factory { get; } = factory;

        public Guid TenantId => tenant.TenantId;

        public static async Task<Fixture> StartAsync()
        {
            var database = await StartDatabaseAsync();
            var factory = new QepApiFactory(database.GetConnectionString());
            var tenant = await RegisterTenantAsync(factory);
            await EnablePipelineAsync(database.GetConnectionString(), tenant.TenantId);
            var owner = await OwnerMembershipIdAsync(database.GetConnectionString(), tenant);
            return new Fixture(database, factory, tenant, owner);
        }

        public async Task<Guid> CustomerAsync(string name = "Laura") => (await SeedCustomerAsync(Factory, TenantId, name)).Id;

        public Task<Guid> ConversationAsync(Guid? customerId) => SeedConversationAsync(Factory, ConnectionString, TenantId, customerId);

        public async Task<SalesPipeline> PipelineAsync()
        {
            using var scope = Factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IPipelineProvisioner>().EnsureAsync(TenantId, Ct);
        }

        /// <summary>An open deal in the first open stage; from <paramref name="conversationId"/> when given (source Conversation).</summary>
        public async Task<Guid> DealAsync(Guid customerId, Guid? conversationId = null)
        {
            using var scope = Factory.Services.CreateScope();
            var pipeline = await scope.ServiceProvider.GetRequiredService<IPipelineProvisioner>().EnsureAsync(TenantId, Ct);
            var deal = Deal.Create(
                TenantId, pipeline, customerId, owner, "Negocio", null, "COP", null,
                conversationId is null ? DealSource.Manual : DealSource.Conversation, conversationId, DealActor.ForUser(owner),
                DateTimeOffset.UtcNow);
            scope.ServiceProvider.GetRequiredService<IDealRepository>().Add(deal);
            await scope.ServiceProvider.GetRequiredService<IPipelineUnitOfWork>().SaveChangesAsync(Ct);
            return deal.Id;
        }

        public Task<Guid> StageOfAsync(Guid dealId) =>
            ScalarAsync<Guid>(ConnectionString, "SELECT stage_id FROM pipeline.deals WHERE id = @id", ("id", dealId));

        public Task<long> MovedAuditCountAsync(Guid dealId) =>
            CountAsync(ConnectionString,
                "SELECT count(*) FROM audit.entries WHERE tenant_id = @t AND action = 'pipeline.deal.moved' AND resource_id = @d "
                + "AND actor_type = 'System' AND actor_id = @empty AND source = 'chatbot'",
                ("t", TenantId), ("d", dealId.ToString()), ("empty", Guid.Empty));

        public Task<long> AnyMovedAuditCountAsync() =>
            CountAsync(ConnectionString,
                "SELECT count(*) FROM audit.entries WHERE tenant_id = @t AND action = 'pipeline.deal.moved'", ("t", TenantId));

        public static async Task<ChatbotDealMoveOutcome> MoveAsync(
            IServiceProvider services, Guid tenantId, Guid? customerId, Guid conversationId, Guid stageId)
        {
            using var scope = services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IChatbotDealMover>()
                .MoveAsync(tenantId, customerId, conversationId, stageId, Ct);
        }

        public Task<ChatbotDealMoveOutcome> MoveAsync(Guid? customerId, Guid conversationId, Guid stageId) =>
            MoveAsync(Factory.Services, TenantId, customerId, conversationId, stageId);

        public async ValueTask DisposeAsync()
        {
            await Factory.DisposeAsync();
            await database.DisposeAsync();
        }
    }

    private static Guid SecondOpenStage(SalesPipeline pipeline) =>
        pipeline.OrderedStages.Where(stage => stage.Kind == StageKind.Open).Skip(1).First().Id;

    [Fact]
    public async Task MovesTheConversationDeal()
    {
        await using var fixture = await Fixture.StartAsync();
        var customer = await fixture.CustomerAsync();
        var conversation = await fixture.ConversationAsync(customer);
        var deal = await fixture.DealAsync(customer, conversation);
        var target = SecondOpenStage(await fixture.PipelineAsync());

        var outcome = await fixture.MoveAsync(customer, conversation, target);

        Assert.Equal(ChatbotDealMoveOutcome.Moved, outcome);
        Assert.Equal(target, await fixture.StageOfAsync(deal));
        Assert.Equal(1L, await fixture.MovedAuditCountAsync(deal));
        Assert.Equal(1L, await CountAsync(fixture.ConnectionString,
            "SELECT count(*) FROM pipeline.deal_stage_history WHERE deal_id = @d AND to_stage_id = @s AND actor_kind = 'System'",
            ("d", deal), ("s", target)));
    }

    [Fact]
    public async Task WithoutALinkedCustomerTheConversationDealIsStillMoved()
    {
        await using var fixture = await Fixture.StartAsync();
        var customer = await fixture.CustomerAsync();
        var conversation = await fixture.ConversationAsync(null);
        var deal = await fixture.DealAsync(customer, conversation);
        var target = SecondOpenStage(await fixture.PipelineAsync());

        Assert.Equal(ChatbotDealMoveOutcome.Moved, await fixture.MoveAsync(null, conversation, target));
        Assert.Equal(target, await fixture.StageOfAsync(deal));
    }

    [Fact]
    public async Task SameStageIsAlreadyInStageAndWritesNoAudit()
    {
        await using var fixture = await Fixture.StartAsync();
        var customer = await fixture.CustomerAsync();
        var conversation = await fixture.ConversationAsync(customer);
        var deal = await fixture.DealAsync(customer, conversation);
        var current = await fixture.StageOfAsync(deal);

        Assert.Equal(ChatbotDealMoveOutcome.AlreadyInStage, await fixture.MoveAsync(customer, conversation, current));
        Assert.Equal(0L, await fixture.AnyMovedAuditCountAsync());
    }

    [Fact]
    public async Task TwoOpenDealsAndNoneFromTheConversationIsSeveralOpenDeals()
    {
        await using var fixture = await Fixture.StartAsync();
        var customer = await fixture.CustomerAsync();
        var conversation = await fixture.ConversationAsync(customer);
        var first = await fixture.DealAsync(customer);
        var second = await fixture.DealAsync(customer);
        var before = await fixture.StageOfAsync(first);
        var target = SecondOpenStage(await fixture.PipelineAsync());

        Assert.Equal(ChatbotDealMoveOutcome.SeveralOpenDeals, await fixture.MoveAsync(customer, conversation, target));
        Assert.Equal(before, await fixture.StageOfAsync(first));
        Assert.Equal(before, await fixture.StageOfAsync(second));
        Assert.Equal(0L, await fixture.AnyMovedAuditCountAsync());
    }

    [Fact]
    public async Task TheConversationDealWinsOverOtherOpenDealsOfTheCustomer()
    {
        await using var fixture = await Fixture.StartAsync();
        var customer = await fixture.CustomerAsync();
        var conversation = await fixture.ConversationAsync(customer);
        var other = await fixture.DealAsync(customer);
        var fromConversation = await fixture.DealAsync(customer, conversation);
        var before = await fixture.StageOfAsync(other);
        var target = SecondOpenStage(await fixture.PipelineAsync());

        Assert.Equal(ChatbotDealMoveOutcome.Moved, await fixture.MoveAsync(customer, conversation, target));
        Assert.Equal(target, await fixture.StageOfAsync(fromConversation));
        Assert.Equal(before, await fixture.StageOfAsync(other));
    }

    [Fact]
    public async Task DealFromTheConversationOfAnotherCustomerIsNotMoved()
    {
        await using var fixture = await Fixture.StartAsync();
        var customer = await fixture.CustomerAsync("Laura");
        var someoneElse = await fixture.CustomerAsync("Sofía");
        var conversation = await fixture.ConversationAsync(customer);
        var foreign = await fixture.DealAsync(someoneElse, conversation);
        var before = await fixture.StageOfAsync(foreign);
        var target = SecondOpenStage(await fixture.PipelineAsync());

        Assert.Equal(ChatbotDealMoveOutcome.NoOpenDeal, await fixture.MoveAsync(customer, conversation, target));
        Assert.Equal(before, await fixture.StageOfAsync(foreign));
        Assert.Equal(0L, await fixture.AnyMovedAuditCountAsync());
    }

    [Fact]
    public async Task OneOpenDealOfTheCustomerIsMovedWhenNoneFromTheConversation()
    {
        await using var fixture = await Fixture.StartAsync();
        var customer = await fixture.CustomerAsync();
        var conversation = await fixture.ConversationAsync(customer);
        var deal = await fixture.DealAsync(customer);
        var target = SecondOpenStage(await fixture.PipelineAsync());

        Assert.Equal(ChatbotDealMoveOutcome.Moved, await fixture.MoveAsync(customer, conversation, target));
        Assert.Equal(target, await fixture.StageOfAsync(deal));
        Assert.Equal(1L, await fixture.MovedAuditCountAsync(deal));
    }

    [Fact]
    public async Task NoCustomerAndNoConversationDealIsNoOpenDeal()
    {
        await using var fixture = await Fixture.StartAsync();
        var conversation = await fixture.ConversationAsync(null);
        var target = SecondOpenStage(await fixture.PipelineAsync());

        Assert.Equal(ChatbotDealMoveOutcome.NoOpenDeal, await fixture.MoveAsync(null, conversation, target));
    }

    [Fact]
    public async Task AMergedCustomerResolvesToTheSurvivorsDeal()
    {
        await using var fixture = await Fixture.StartAsync();
        var survivor = await fixture.CustomerAsync("Laura");
        var merged = await fixture.CustomerAsync("Laura G");
        await MarkMergedAsync(fixture.ConnectionString, merged, survivor);
        var conversation = await fixture.ConversationAsync(merged);
        var deal = await fixture.DealAsync(survivor);
        var target = SecondOpenStage(await fixture.PipelineAsync());

        Assert.Equal(ChatbotDealMoveOutcome.Moved, await fixture.MoveAsync(merged, conversation, target));
        Assert.Equal(target, await fixture.StageOfAsync(deal));
    }

    [Fact]
    public async Task ClosedStageIsStageNotOpen()
    {
        await using var fixture = await Fixture.StartAsync();
        var customer = await fixture.CustomerAsync();
        var conversation = await fixture.ConversationAsync(customer);
        var deal = await fixture.DealAsync(customer, conversation);
        var before = await fixture.StageOfAsync(deal);
        var won = (await fixture.PipelineAsync()).WonStage.Id;

        Assert.Equal(ChatbotDealMoveOutcome.StageNotOpen, await fixture.MoveAsync(customer, conversation, won));
        Assert.Equal(before, await fixture.StageOfAsync(deal));
    }

    [Fact]
    public async Task UnknownStageIsStageMissing()
    {
        await using var fixture = await Fixture.StartAsync();
        var customer = await fixture.CustomerAsync();
        var conversation = await fixture.ConversationAsync(customer);
        _ = await fixture.DealAsync(customer, conversation);

        Assert.Equal(ChatbotDealMoveOutcome.StageMissing, await fixture.MoveAsync(customer, conversation, Guid.CreateVersion7()));
    }

    [Fact]
    public async Task ModuleOffIsModuleOff()
    {
        await using var fixture = await Fixture.StartAsync();
        var customer = await fixture.CustomerAsync();
        var conversation = await fixture.ConversationAsync(customer);
        var deal = await fixture.DealAsync(customer, conversation);
        var target = SecondOpenStage(await fixture.PipelineAsync());
        await DisableModuleAsync(fixture.ConnectionString, fixture.TenantId, "pipeline");

        Assert.Equal(ChatbotDealMoveOutcome.ModuleOff, await fixture.MoveAsync(customer, conversation, target));
        Assert.NotEqual(target, await fixture.StageOfAsync(deal));
    }

    [Fact]
    public async Task ConflictRetriesOnceThenSucceeds()
    {
        await using var fixture = await Fixture.StartAsync();
        var customer = await fixture.CustomerAsync();
        var conversation = await fixture.ConversationAsync(customer);
        var deal = await fixture.DealAsync(customer, conversation);
        var target = SecondOpenStage(await fixture.PipelineAsync());
        var interference = new Interference(fixture.ConnectionString, deal, times: 1);
        await using var racing = WithInterference(fixture.Factory, interference);

        var outcome = await Fixture.MoveAsync(racing.Services, fixture.TenantId, customer, conversation, target);

        Assert.Equal(ChatbotDealMoveOutcome.Moved, outcome);
        Assert.Equal(2, interference.Saves);
        Assert.Equal(target, await fixture.StageOfAsync(deal));
        // The failed attempt left nothing behind: one audit row, one history row for the move.
        Assert.Equal(1L, await fixture.MovedAuditCountAsync(deal));
        Assert.Equal(1L, await CountAsync(fixture.ConnectionString,
            "SELECT count(*) FROM pipeline.deal_stage_history WHERE deal_id = @d AND to_stage_id = @s", ("d", deal), ("s", target)));
    }

    [Fact]
    public async Task TwoConflictsAreConflict()
    {
        await using var fixture = await Fixture.StartAsync();
        var customer = await fixture.CustomerAsync();
        var conversation = await fixture.ConversationAsync(customer);
        var deal = await fixture.DealAsync(customer, conversation);
        var before = await fixture.StageOfAsync(deal);
        var target = SecondOpenStage(await fixture.PipelineAsync());
        var interference = new Interference(fixture.ConnectionString, deal, times: 2);
        await using var racing = WithInterference(fixture.Factory, interference);

        var outcome = await Fixture.MoveAsync(racing.Services, fixture.TenantId, customer, conversation, target);

        Assert.Equal(ChatbotDealMoveOutcome.Conflict, outcome);
        Assert.Equal(2, interference.Saves);
        Assert.Equal(before, await fixture.StageOfAsync(deal));
        Assert.Equal(0L, await fixture.AnyMovedAuditCountAsync());
    }

    /// <summary>Another writer bumps the deal's version right before each of the first <c>times</c> saves, as a person moving
    /// the same deal on the board would: the save then fails with <c>concurrency.conflict</c>.</summary>
    private sealed class Interference(string connectionString, Guid dealId, int times)
    {
        private int remaining = times;

        public int Saves { get; private set; }

        public async Task BeforeSaveAsync()
        {
            Saves++;
            if (remaining > 0)
            {
                remaining--;
                Assert.Equal(1, await ExecuteAsync(connectionString,
                    "UPDATE pipeline.deals SET version = version + 1 WHERE id = @id", ("id", dealId)));
            }
        }
    }

    private sealed class InterferingUnitOfWork(IPipelineUnitOfWork inner, Interference interference) : IPipelineUnitOfWork
    {
        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
        {
            await interference.BeforeSaveAsync();
            return await inner.SaveChangesAsync(cancellationToken);
        }

        public void DiscardChanges() => inner.DiscardChanges();
    }

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> WithInterference(
        QepApiFactory factory, Interference interference) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddScoped<IPipelineUnitOfWork>(provider =>
                new InterferingUnitOfWork(new PipelineUnitOfWork(provider.GetRequiredService<PipelineDbContext>()), interference))));
}
