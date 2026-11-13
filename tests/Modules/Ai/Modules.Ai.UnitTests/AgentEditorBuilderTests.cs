using Modules.Ai.Application;
using Modules.Ai.Domain;

namespace Modules.Ai.UnitTests;

/// <summary>Plan 2026-11-12 (correcciones de la revisión), Tarea 2: Integrations borra una conexión sin avisar, y la referencia de
/// <c>ai.agent_connections</c> es blanda. Una conexión que ya no existe no cuenta para publicar ni vuelve al editor.</summary>
public sealed class AgentEditorBuilderTests
{
    private static readonly Guid Tenant = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid By = Guid.Parse("01900000-0000-7000-8000-000000000002");
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ADeletedConnectionDoesNotCountForPublishing()
    {
        var deletedId = Guid.CreateVersion7();
        var agent = NewAgent();
        agent.SetConnections([deletedId], Now);

        var issues = await Builder(new FakeConnections()).ValidateAsync(agent, publishing: true, Ct);

        Assert.Contains(issues, issue => issue.Code == "connections_missing");
    }

    [Fact]
    public async Task TheEditorOnlyReturnsLiveConnections()
    {
        var liveId = Guid.CreateVersion7();
        var deletedId = Guid.CreateVersion7();
        var agent = NewAgent();
        agent.SetConnections([liveId, deletedId], Now);

        var editor = await Builder(new FakeConnections(new AiConnectionRef(liveId, "Ventas", "WhatsApp", "+57 300", "Active")))
            .BuildAsync(agent, Ct);

        Assert.Equal([liveId], editor.ConnectionIds);
        Assert.DoesNotContain(editor.Issues, issue => issue.Code == "connections_missing");
    }

    // Plan 2026-11-12 (correcciones), Tarea 5: el autoguardado valida el borrador en cada pausa al escribir. Para contar las conexiones
    // vivas bastan los ids; la lectura completa resuelve cada conexión activa con su secreto.
    [Fact]
    public async Task ValidatingReadsOnlyConnectionIds()
    {
        var liveId = Guid.CreateVersion7();
        var agent = NewAgent();
        agent.SetConnections([liveId], Now);
        var connections = new FakeConnections(new AiConnectionRef(liveId, "Ventas", "WhatsApp", "+57 300", "Active"));

        var issues = await Builder(connections).ValidateAsync(agent, publishing: false, Ct);

        Assert.DoesNotContain(issues, issue => issue.Code == "connections_missing");
        Assert.Equal((0, 1), (connections.ListCalls, connections.ListIdsCalls));
    }

    private static Agent NewAgent() =>
        Agent.Create(Tenant, "Asesor virtual", AgentConfig.Default(new AgentModelRef("anthropic", "claude-opus-5-5")), By, Now);

    private static AgentEditorBuilder Builder(IAiConnections connections) =>
        new(connections, new FakeSlots(), new FakeMembers(), new FakeModelCatalog());

    private sealed class FakeConnections(params AiConnectionRef[] connections) : IAiConnections
    {
        public int ListCalls { get; private set; }

        public int ListIdsCalls { get; private set; }

        public Task<IReadOnlyList<AiConnectionRef>> ListAsync(Guid tenantId, CancellationToken cancellationToken)
        {
            ListCalls++;
            return Task.FromResult<IReadOnlyList<AiConnectionRef>>(connections);
        }

        public Task<IReadOnlySet<Guid>> ListIdsAsync(Guid tenantId, CancellationToken cancellationToken)
        {
            ListIdsCalls++;
            return Task.FromResult<IReadOnlySet<Guid>>(connections.Select(connection => connection.Id).ToHashSet());
        }
    }

    private sealed class FakeSlots : IAgentConnectionSlots
    {
        public Task<IReadOnlyList<AiConnectionOccupant>> SyncAgentsAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AiConnectionOccupant>>([]);

        public Task<IReadOnlyList<AiConnectionOccupant>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AiConnectionOccupant>>([]);
    }

    private sealed class FakeMembers : IAiMembers
    {
        public Task<IReadOnlyList<AiMemberRef>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AiMemberRef>>([]);
    }

    private sealed class FakeModelCatalog : IAiModelCatalog
    {
        public AiProvider DefaultProvider => AiProviders.Anthropic;

        public IReadOnlyList<AiProviderModels> Providers { get; } =
        [
            new(AiProviders.Anthropic,
                [new("claude-opus-5-5", "Claude Opus 5.5", "El más capaz.", IsDefault: true, Available: true, Effort: "medium")]),
            new(AiProviders.OpenAi, [new("gpt-6-luna", "GPT-6 Luna", "", IsDefault: true, Available: false, Effort: null)]),
        ];
    }
}
