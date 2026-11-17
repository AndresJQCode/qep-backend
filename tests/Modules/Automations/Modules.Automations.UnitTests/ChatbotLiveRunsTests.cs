using Modules.Automations.Application;
using Modules.Automations.Application.Engine;
using Modules.Automations.Domain;
using Modules.Automations.Domain.Engine;

namespace Modules.Automations.UnitTests;

/// <summary>Spec 2026-11-03 D-7: borrar un chatbot sólo se bloquea mientras el bot de verdad atiende alguna conversación.</summary>
public sealed class ChatbotLiveRunsTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ChatbotId = Guid.CreateVersion7();
    private static readonly Guid ConnectionId = Guid.CreateVersion7();
    private static readonly DateTimeOffset StartedAt = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static ConversationRun Run(Guid conversationId, RunStatus status = RunStatus.Waiting) =>
        ConversationRun.Start(
            Guid.CreateVersion7(), TenantId, conversationId, ConnectionId, ChatbotId, Guid.CreateVersion7(),
            RunState.Initial(RunVariables.Empty()) with { Status = status }, StartedAt);

    private static AutomationConversationState State(
        string status = "Open", Guid? member = null, string automation = "Active", string? pauseReason = null) =>
        new(status, member, automation, ChatbotId, ConnectionId, StartedAt, "Laura", "573001234567", null, pauseReason);

    [Theory]
    [InlineData("resolved")]
    [InlineData("taken")]
    [InlineData("pausedManual")]
    [InlineData("missing")]
    public async Task AWaitingRunNobodyAttendsDoesNotBlock(string state)
    {
        var conversationId = Guid.CreateVersion7();
        var conversations = new Conversations();
        switch (state)
        {
            case "resolved":
                conversations.Add(conversationId, State(status: "Resolved"));
                break;
            case "taken":
                conversations.Add(conversationId, State(member: Guid.CreateVersion7(), automation: "Paused", pauseReason: "AdvisorTook"));
                break;
            case "pausedManual":
                conversations.Add(conversationId, State(automation: "Paused", pauseReason: "Manual"));
                break;
        }

        Assert.False(await ChatbotLiveRuns.AnyAttendingAsync(new Runs(Run(conversationId)), conversations, TenantId, ChatbotId, Ct));
    }

    [Fact]
    public async Task AWaitingRunTheBotAttendsBlocks()
    {
        var conversationId = Guid.CreateVersion7();
        var conversations = new Conversations();
        conversations.Add(conversationId, State());

        Assert.True(await ChatbotLiveRuns.AnyAttendingAsync(new Runs(Run(conversationId)), conversations, TenantId, ChatbotId, Ct));
    }

    [Fact]
    public async Task ARunningRunWithoutAPersonBlocks()
    {
        var conversationId = Guid.CreateVersion7();
        var conversations = new Conversations();
        conversations.Add(conversationId, State());

        Assert.True(await ChatbotLiveRuns.AnyAttendingAsync(
            new Runs(Run(conversationId, RunStatus.Running)), conversations, TenantId, ChatbotId, Ct));
    }

    [Fact]
    public async Task TheFirstAttendingRunStopsTheReads()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var conversations = new Conversations();
        conversations.Add(first, State());
        conversations.Add(second, State());

        Assert.True(await ChatbotLiveRuns.AnyAttendingAsync(
            new Runs(Run(first), Run(second)), conversations, TenantId, ChatbotId, Ct));
        Assert.Equal(1, conversations.Reads);
    }

    [Fact]
    public async Task ANonAttendingRunBeforeAnAttendingOneStillBlocks()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var conversations = new Conversations();
        conversations.Add(first, State(member: Guid.CreateVersion7(), automation: "Paused", pauseReason: "AdvisorTook"));
        conversations.Add(second, State());

        Assert.True(await ChatbotLiveRuns.AnyAttendingAsync(
            new Runs(Run(first), Run(second)), conversations, TenantId, ChatbotId, Ct));
        Assert.Equal(2, conversations.Reads);
    }

    [Fact]
    public async Task NoLiveRunsReadsNothing()
    {
        var conversations = new Conversations();

        Assert.False(await ChatbotLiveRuns.AnyAttendingAsync(new Runs(), conversations, TenantId, ChatbotId, Ct));
        Assert.Equal(0, conversations.Reads);
    }

    private sealed class Runs(params ConversationRun[] live) : IConversationRunQueries
    {
        public Task<IReadOnlyDictionary<Guid, (int Handled, int HandedOff)>> TodayAsync(
            Guid tenantId, IReadOnlyCollection<Guid> chatbotIds, DateTimeOffset dayStartUtc, DateTimeOffset dayEndUtc,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<ConversationRun>> ListLiveAsync(Guid tenantId, Guid chatbotId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ConversationRun>>(live);
    }

    private sealed class Conversations : IAutomationConversations
    {
        private readonly Dictionary<Guid, AutomationConversationState> byId = [];

        public int Reads { get; private set; }

        public void Add(Guid conversationId, AutomationConversationState state) => byId[conversationId] = state;

        public Task<AutomationConversationState?> GetAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(byId.GetValueOrDefault(conversationId));
        }

        public Task<bool> WasResolvedSinceAsync(Guid tenantId, Guid conversationId, DateTimeOffset since, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> WasTakenSinceAsync(Guid tenantId, Guid conversationId, DateTimeOffset since, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task StartAsync(Guid tenantId, Guid conversationId, Guid chatbotId, string? firstStepTitle, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SetStepAsync(Guid tenantId, Guid conversationId, string? stepTitle, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SetChatbotAsync(Guid tenantId, Guid conversationId, Guid chatbotId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task HandoffAsync(Guid tenantId, Guid conversationId, HandoffCause cause, Guid? assignToMemberId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ResolveAsync(Guid tenantId, Guid conversationId, Guid chatbotId, Guid? reasonId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task EndAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
