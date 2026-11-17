using Modules.Automations.Application;
using Modules.Automations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Automations.UnitTests;

/// <summary>Spec 2026-11-17 (agente IA en chatbots) §7: el editor trae los agentes del tenant y si tiene el módulo <c>ai</c>; la lista, el
/// consumo de IA del mes por chatbot.</summary>
public sealed class ChatbotEditorBuilderTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string EmptyFlow = """{ "schema": 1, "start": { "firstNodeId": null }, "variables": [], "nodes": [] }""";

    private static Chatbot NewChatbot() =>
        Chatbot.Create(TenantId, "Bienvenida", string.Empty, ChatbotTrigger.NewConversation, [], EmptyFlow, "{}", Guid.CreateVersion7(), DateTimeOffset.UtcNow);

    [Fact]
    public async Task TheEditorListsTheTenantAgentsOnlyWithAi()
    {
        var agent = new AutomationAgentRef(Guid.CreateVersion7(), "Ventas", "Paused", "anthropic", "claude-sonnet-5-5");
        var directory = new Directory([agent]);

        var withAi = await Builder(directory, aiEnabled: true).BuildAsync(NewChatbot(), Ct);
        var withoutAi = await Builder(directory, aiEnabled: false).BuildAsync(NewChatbot(), Ct);

        Assert.True(withAi.AiEnabled);
        Assert.Equal([new ChatbotEditorAgentDto(agent.Id, "Ventas", "Paused", "anthropic", "claude-sonnet-5-5")], withAi.Agents);
        Assert.False(withoutAi.AiEnabled);
        Assert.Empty(withoutAi.Agents);
    }

    [Fact]
    public async Task TheListSumsTheAiUsageOfTheMonthPerChatbot()
    {
        var used = Guid.CreateVersion7();
        var unused = Guid.CreateVersion7();
        var directory = new Directory([]) { Usage = { [used] = (1200, 340) } };
        var rows = new[] { Row(used), Row(unused) };

        var list = await Builder(directory, aiEnabled: true).BuildListAsync(TenantId, rows, Ct);

        Assert.Equal(new ChatbotAiUsageDto(1200, 340), list.Items.Single(item => item.Id == used).AiUsageThisMonth);
        Assert.Null(list.Items.Single(item => item.Id == unused).AiUsageThisMonth);
        // §7: el mes del reloj del tenant (Bogotá, UTC-5): del 1 a las 05:00 UTC al 1 del mes siguiente.
        Assert.Equal((new DateTimeOffset(2026, 11, 1, 5, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 12, 1, 5, 0, 0, TimeSpan.Zero)), directory.Window);
    }

    private static ChatbotSummaryRow Row(Guid id) =>
        new(id, "Bot", string.Empty, ChatbotStatus.Draft, ChatbotTrigger.NewConversation, [], null, null, DateTimeOffset.UtcNow, 1);

    private static ChatbotEditorBuilder Builder(Directory directory, bool aiEnabled) =>
        new(new NoConnections(), new NoExternalApis(), new NoMembers(), new NoHours(), new NoRuns(), new BogotaClock(), new NoSlots(), directory,
            new Modules(aiEnabled));

    private sealed class Directory(IReadOnlyList<AutomationAgentRef> agents) : IAutomationsAgentDirectory
    {
        public Dictionary<Guid, (long Input, long Output)> Usage { get; } = [];

        public (DateTimeOffset From, DateTimeOffset To)? Window { get; private set; }

        public Task<IReadOnlyList<AutomationAgentRef>> ListAsync(Guid tenantId, CancellationToken cancellationToken) => Task.FromResult(agents);

        public Task<IReadOnlyDictionary<Guid, (long InputTokens, long OutputTokens)>> UsageByChatbotAsync(
            Guid tenantId, DateTimeOffset from, DateTimeOffset until, CancellationToken cancellationToken)
        {
            Window = (from, until);
            return Task.FromResult<IReadOnlyDictionary<Guid, (long, long)>>(Usage.ToDictionary(pair => pair.Key, pair => pair.Value));
        }
    }

    private sealed class Modules(bool aiEnabled) : ITenantModules
    {
        public Task<TenantModuleSet?> FindAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<TenantModuleSet?>(TenantModuleSet.FromStored(
                aiEnabled ? [TenantModuleKeys.Messaging, TenantModuleKeys.Automations, TenantModuleKeys.Ai] : [TenantModuleKeys.Messaging, TenantModuleKeys.Automations]));
    }

    private sealed class BogotaClock : ITenantClock
    {
        public Task<TenantCalendar> GetAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(new TenantCalendar(
                new DateTimeOffset(2026, 11, 17, 15, 0, 0, TimeSpan.Zero), TimeZoneInfo.FindSystemTimeZoneById("America/Bogota")));
    }

    private sealed class NoConnections : IAutomationConnections
    {
        public Task<IReadOnlyList<AutomationConnectionRef>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AutomationConnectionRef>>([]);
    }

    private sealed class NoExternalApis : IAutomationExternalApis
    {
        public Task<IReadOnlyList<ExternalApiRef>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExternalApiRef>>([]);

        public Task<ExternalApiResolution> ResolveAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class NoMembers : IAutomationMembers
    {
        public Task<IReadOnlyList<AutomationMemberRef>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AutomationMemberRef>>([]);
    }

    private sealed class NoHours : IAutomationBusinessHours
    {
        public Task<IReadOnlyDictionary<Guid, string?>> ScheduleNamesAsync(Guid tenantId, IReadOnlyCollection<Guid> connectionIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string?>>(new Dictionary<Guid, string?>());

        public Task<bool> IsOpenAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class NoRuns : IConversationRunQueries
    {
        public Task<IReadOnlyDictionary<Guid, (int Handled, int HandedOff)>> TodayAsync(
            Guid tenantId, IReadOnlyCollection<Guid> chatbotIds, DateTimeOffset dayStartUtc, DateTimeOffset dayEndUtc, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, (int, int)>>(new Dictionary<Guid, (int, int)>());

        public Task<IReadOnlyList<ConversationRun>> ListLiveAsync(Guid tenantId, Guid chatbotId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class NoSlots : IChatbotConnectionSlots
    {
        public Task<IReadOnlyList<ConnectionOccupant>> SyncChatbotsAsync(Guid tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ConnectionOccupant>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ConnectionOccupant>>([]);
    }
}
