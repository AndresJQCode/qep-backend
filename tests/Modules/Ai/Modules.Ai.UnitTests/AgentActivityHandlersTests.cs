using System.Text.Json;
using BuildingBlocks.Application;
using Modules.Ai.Application;
using Modules.Ai.Domain;
using Modules.Tenancy.Domain;

namespace Modules.Ai.UnitTests;

/// <summary>
/// Spec 2026-10-29 §5 (<c>GET /conversations/{id}/agent-activity</c> y <c>GET /sessions</c>) y plan 2026-11-12 Tarea 9: cada respuesta del agente
/// muestra el proveedor y el modelo que de verdad respondió (C-4), sus fuentes ya resueltas a algo que se pueda leer y sus herramientas.
/// </summary>
public sealed class AgentActivityHandlersTests
{
    private static readonly Guid Tenant = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid Conversation = Guid.Parse("01900000-0000-7000-8000-0000000000f1");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EachSourceIsResolvedToAFaqAQuickReplyOrAToolCall()
    {
        var reader = new FakeReader { Conversation = [Session(["envios", "qr-1", "call_1", "qr-borrada"])] };

        var activity = await Handler(reader).HandleAsync(new GetConversationAgentActivityQuery(Tenant, Conversation), Ct);

        var response = Assert.Single(Assert.Single(activity.Sessions).Responses);
        Assert.Equal(
            [
                new AgentActivitySourceDto("faq", "envios", "¿Hacen envíos?"),
                new AgentActivitySourceDto("quickReply", "qr-1", "/envio"),
                new AgentActivitySourceDto("tool", "call_1", AgentToolNames.SearchCatalog),
                new AgentActivitySourceDto("quickReply", "qr-borrada", null),
            ],
            response.Sources);
    }

    // C-4: el proveedor es el de la versión de la sesión; los modelos servidos, los que reportó cada llamada (con el respaldo de C-9 pueden
    // ser dos).
    [Fact]
    public async Task EachResponseCarriesTheProviderAndTheServedModels()
    {
        var reader = new FakeReader { Conversation = [Session([])] };

        var response = Assert.Single(Assert.Single(
            (await Handler(reader).HandleAsync(new GetConversationAgentActivityQuery(Tenant, Conversation), Ct)).Sessions).Responses);

        Assert.Equal(("anthropic", ["claude-sonnet-5-5", "claude-opus-5-5"]), (response.Provider, response.ServedModels));
        Assert.Equal(new AgentUsageDto("anthropic", 2, 3000, 120, 800, null), response.Usage);
    }

    [Fact]
    public async Task WithoutReadPermissionTheActivityIsDenied()
    {
        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            new GetConversationAgentActivityHandler(new FakeExecutionContext(Tenant), AllModules(), new FakeReader(), new FakeQuickReplies())
                .HandleAsync(new GetConversationAgentActivityQuery(Tenant, Conversation), Ct));

        Assert.Equal("authorization.denied", error.Code);
    }

    [Fact]
    public async Task TheSessionHistoryPassesItsFiltersAndCap()
    {
        var reader = new FakeReader();
        var agent = Guid.CreateVersion7();
        var from = new DateTimeOffset(2026, 11, 1, 5, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 11, 15, 5, 0, 0, TimeSpan.Zero);

        await new ListAgentSessionsHandler(
                new FakeExecutionContext(Tenant, AiPermissions.AgentRead), AllModules(), reader, new ListAgentSessionsValidator())
            .HandleAsync(new ListAgentSessionsQuery(Tenant, agent, from, to), Ct);

        Assert.Equal((agent, from, to, ListAgentSessionsHandler.MaxSessions), reader.SessionsAsked);
    }

    [Fact]
    public void ARangeThatEndsBeforeItStartsIsInvalid() =>
        Assert.False(new ListAgentSessionsValidator().Validate(new ListAgentSessionsQuery(
            Tenant, null, new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero))).IsValid);

    private static GetConversationAgentActivityHandler Handler(FakeReader reader) =>
        new(new FakeExecutionContext(Tenant, AiPermissions.AgentRead), AllModules(), reader, new FakeQuickReplies());

    private static FakeTenantModules AllModules() => new(TenantModuleSet.FromStored(TenantModuleKeys.All));

    private static AgentConversationSession Session(IReadOnlyList<string> sources)
    {
        var config = AgentConfig.Default(new AgentModelRef("anthropic", "claude-sonnet-5-5")) with
        {
            Instructions = "Atiende.",
            Faq = [new AgentFaq("envios", "¿Hacen envíos?", "Sí.")],
            UseQuickReplies = true,
        };
        var at = new DateTimeOffset(2026, 11, 15, 14, 0, 0, TimeSpan.Zero);
        return new AgentConversationSession(
            Guid.CreateVersion7(), Guid.CreateVersion7(), "Asesor virtual", 1, "anthropic", "claude-sonnet-5-5", config.ToJson(), "Live", null, null, at,
            null,
            [
                new AgentConversationJob(
                    Guid.CreateVersion7(), "Done", "replied", null, at, at.AddSeconds(5), [Guid.CreateVersion7()], false, sources,
                    [
                        new AgentTestToolCallDto(
                            "call_1", AgentToolNames.SearchCatalog, JsonDocument.Parse("""{"query":"jabón","limit":3}""").RootElement, null, "ok", null, 40, at),
                    ],
                    ["claude-sonnet-5-5", "claude-opus-5-5"],
                    new AgentUsageDto("anthropic", 2, 3000, 120, 800, null)),
            ]);
    }

    private sealed class FakeReader : IAgentActivityReader
    {
        public IReadOnlyList<AgentConversationSession> Conversation { get; init; } = [];

        public (Guid? Agent, DateTimeOffset? From, DateTimeOffset? To, int Limit) SessionsAsked { get; private set; }

        public Task<AgentListActivity> SummarizeAsync(Guid tenantId, AgentActivityWindow window, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AgentSessionDto>> ListSessionsAsync(
            Guid tenantId, Guid? agentId, DateTimeOffset? startedFrom, DateTimeOffset? startedBefore, int limit, CancellationToken cancellationToken)
        {
            SessionsAsked = (agentId, startedFrom, startedBefore, limit);
            return Task.FromResult<IReadOnlyList<AgentSessionDto>>([]);
        }

        public Task<IReadOnlyList<AgentConversationSession>> LoadConversationAsync(
            Guid tenantId, Guid conversationId, CancellationToken cancellationToken) => Task.FromResult(Conversation);
    }

    private sealed class FakeQuickReplies : IAiQuickReplies
    {
        public Task<IReadOnlyList<AiQuickReply>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AiQuickReply>>([new AiQuickReply("qr-1", "/envio", "Enviamos a todo el país.")]);
    }
}
