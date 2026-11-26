using BuildingBlocks.Application;
using BuildingBlocks.Domain;
using Modules.Ai.Application;
using Modules.Ai.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Ai.UnitTests;

/// <summary>Spec 2026-11-17 D-B18: borrar un agente que usa un chatbot publicado es 409 con los chatbots, después de mirar las sesiones vivas
/// (el orden del plan, B2.2). Pausarlo no se bloquea.</summary>
public sealed class DeleteAgentHandlerTests
{
    private static readonly Guid Tenant = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid By = Guid.Parse("01900000-0000-7000-8000-000000000002");
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DeletingAnAgentUsedByAPublishedChatbotIsAConflictWithTheChatbots()
    {
        var agent = NewAgent();
        var repository = new FakeAgents(agent);
        var chatbot = new AiChatbotRef(Guid.CreateVersion7(), "Bienvenida");
        var usage = new FakeChatbotUsage(new Dictionary<Guid, IReadOnlyList<AiChatbotRef>> { [agent.Id] = [chatbot] });

        var error = await Assert.ThrowsAsync<AgentInUseByChatbotsException>(() =>
            Handler(repository, usage).HandleAsync(new DeleteAgentCommand(Tenant, agent.Id, agent.Version), Ct));

        Assert.Equal(AiErrorCodes.AgentInUseByChatbots, error.Code);
        Assert.Equal("ai.agent.in_use_by_chatbots", error.Code);
        Assert.IsAssignableFrom<IHasProblemExtensions>(error);
        Assert.Equal([chatbot], (IEnumerable<AiChatbotRef>)error.ProblemExtensions["chatbots"]!);
        Assert.Empty(repository.Removed);
    }

    [Fact]
    public async Task AnAgentNoChatbotUsesIsDeleted()
    {
        var agent = NewAgent();
        var repository = new FakeAgents(agent);

        Assert.True(await Handler(repository, new FakeChatbotUsage(new Dictionary<Guid, IReadOnlyList<AiChatbotRef>>())).HandleAsync(new DeleteAgentCommand(Tenant, agent.Id, agent.Version), Ct));
        Assert.Equal([agent], repository.Removed);
    }

    private static Agent NewAgent() =>
        Agent.Create(Tenant, "Asesor virtual", AgentConfig.Default(new AgentModelRef("anthropic", "claude-opus-5-5")), By, Now);

    private static DeleteAgentHandler Handler(FakeAgents agents, FakeChatbotUsage usage) =>
        new(
            new FakeExecutionContext(Tenant, AiPermissions.AgentManage),
            new FakeTenantModules(TenantModuleSet.FromStored(TenantModuleKeys.All)),
            agents,
            new AgentSessionProcessorTests.FakeSessions(),
            usage,
            new NoUnitOfWork(),
            new AgentSessionProcessorTests.FakeAudit(),
            new AgentSessionProcessorTests.FixedClock(Now));

    private sealed class FakeChatbotUsage(IReadOnlyDictionary<Guid, IReadOnlyList<AiChatbotRef>> usage) : IAiChatbotUsage
    {
        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<AiChatbotRef>>> ListByAgentAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(usage);
    }

    private sealed class FakeAgents(Agent agent) : IAgentRepository
    {
        public List<Agent> Removed { get; } = [];

        public Task<Agent?> FindAsync(Guid tenantId, Guid agentId, CancellationToken cancellationToken) =>
            Task.FromResult<Agent?>(agentId == agent.Id ? agent : null);

        public Task<IReadOnlyList<Agent>> ListAsync(Guid tenantId, string? search, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Agent>>([agent]);

        public Task<bool> NameExistsAsync(Guid tenantId, string name, Guid? exceptId, CancellationToken cancellationToken) => Task.FromResult(false);

        public void Add(Agent added) => throw new NotSupportedException();

        public void Remove(Agent removed) => Removed.Add(removed);
    }

    private sealed class NoUnitOfWork : IAiUnitOfWork
    {
        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
