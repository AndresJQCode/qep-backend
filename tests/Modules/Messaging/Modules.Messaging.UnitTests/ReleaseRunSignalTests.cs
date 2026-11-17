using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-11-17 (agente IA en chatbots) §6.4: con una delegación viva, soltar la conversación avisa a los dos bots (la sesión del
/// agente y la corrida del chatbot que espera en el bloque) y marca la delegación <c>Interrupted</c> en el mismo guardado. Sin delegación,
/// la regla de siempre: sólo el bot que la tenía (revisión final de agentes, hallazgo 5).</summary>
public sealed class ReleaseRunSignalTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Member = Guid.CreateVersion7();
    private const string Brief = """{"objective":"x","context":"","firstTurn":"lastMessage","idleMinutes":60}""";

    private static Conversation Open() =>
        Conversation.Start(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "573001234567", "Laura", Now);

    private static (Conversation Conversation, BotDelegation Delegation, FakeDelegations Repository) Delegated()
    {
        var conversation = Open();
        conversation.StartAutomation(Guid.CreateVersion7(), Now);
        var delegation = BotDelegation.Start(
            Guid.CreateVersion7(), conversation.TenantId, conversation.Id, conversation.ConnectionId, conversation.AutomationChatbotId!.Value,
            Guid.CreateVersion7(), "ai-agent-1", Guid.CreateVersion7(), null, Brief, Now);
        conversation.DelegateToAgent(delegation.Id, delegation.AgentId, Now);
        return (conversation, delegation, new FakeDelegations(delegation));
    }

    [Fact]
    public async Task WithALiveDelegationTheReleaseSignalsBothBots()
    {
        var (conversation, delegation, repository) = Delegated();
        var inbox = new FakeInbox();
        var before = conversation.AutomationStatus;
        var beforeBot = conversation.AutomationBotKind;
        var beforeDelegation = conversation.AutomationDelegationId;

        var changes = conversation.Take(Member, Now.AddMinutes(1));
        await inbox.EnqueueIfAssistedAsync(
            repository, conversation, before, beforeBot, beforeDelegation, changes.Count, Now.AddMinutes(1), TestContext.Current.CancellationToken);

        Assert.Equal([BotKind.Agent, BotKind.Chatbot], inbox.Releases.Select(release => release.BotKind));
        Assert.All(inbox.Releases, release => Assert.Equal(conversation.Id, release.ConversationId));
        Assert.Equal(BotDelegationStatus.Interrupted, delegation.Status);
        Assert.Equal(Now.AddMinutes(1), delegation.EndedAt);
    }

    [Theory]
    [InlineData(BotKind.Chatbot)]
    [InlineData(BotKind.Agent)]
    public async Task WithoutDelegationOnlyTheBotThatHadItIsSignaled(BotKind kind)
    {
        var conversation = Open();
        conversation.StartAutomation(new BotRef(kind, Guid.CreateVersion7()), Now);
        var inbox = new FakeInbox();
        var repository = new FakeDelegations();
        var before = conversation.AutomationStatus;
        var beforeBot = conversation.AutomationBotKind;

        conversation.Resolve(Now.AddMinutes(1));
        await inbox.EnqueueIfAssistedAsync(
            repository, conversation, before, beforeBot, conversation.AutomationDelegationId, changes: 1, Now.AddMinutes(1),
            TestContext.Current.CancellationToken);

        Assert.Equal([kind], inbox.Releases.Select(release => release.BotKind));
        Assert.Equal(0, repository.Finds);
    }

    [Fact]
    public async Task WithoutAssistantOrWithoutChangesNothingIsSignaled()
    {
        var (conversation, delegation, repository) = Delegated();
        var inbox = new FakeInbox();

        await inbox.EnqueueIfAssistedAsync(
            repository, conversation, ConversationAutomationStatus.Active, BotKind.Agent, delegation.Id, changes: 0, Now,
            TestContext.Current.CancellationToken);
        await inbox.EnqueueIfAssistedAsync(
            repository, Open(), ConversationAutomationStatus.None, null, null, changes: 1, Now, TestContext.Current.CancellationToken);

        Assert.Empty(inbox.Releases);
        Assert.Equal(BotDelegationStatus.Live, delegation.Status);
    }

    [Fact]
    public async Task ADelegationThatAlreadyReturnedKeepsItsReturnButBothBotsAreSignaled()
    {
        var (conversation, delegation, repository) = Delegated();
        delegation.Return("""{"output":"done","reason":"objective_completed"}""", Now);
        var inbox = new FakeInbox();

        await inbox.EnqueueIfAssistedAsync(
            repository, conversation, ConversationAutomationStatus.Active, BotKind.Agent, delegation.Id, changes: 1, Now.AddMinutes(1),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, inbox.Releases.Count);
        Assert.Equal(BotDelegationStatus.Returned, delegation.Status);
    }

    private sealed record Release(Guid ConversationId, BotKind BotKind);

    private sealed class FakeDelegations(params BotDelegation[] delegations) : IBotDelegationRepository
    {
        public int Finds { get; private set; }

        public Task<BotDelegation?> FindAsync(Guid tenantId, Guid delegationId, CancellationToken cancellationToken)
        {
            Finds++;
            return Task.FromResult(delegations.SingleOrDefault(value => value.TenantId == tenantId && value.Id == delegationId));
        }

        public void Add(BotDelegation delegation) => throw new NotSupportedException();
    }

    private sealed class FakeInbox : IAutomationInbox
    {
        public List<Release> Releases { get; } = [];

        public void EnqueueReleaseRun(Guid tenantId, Guid connectionId, Guid conversationId, BotKind botKind, DateTimeOffset now) =>
            Releases.Add(new Release(conversationId, botKind));

        public Task<IReadOnlyList<AutomationInboxItem>> ClaimAsync(DateTimeOffset now, int batch, BotKind botKind, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> CompleteAsync(Guid tenantId, Guid messageId, int attempts, string outcome, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> RetryLaterAsync(Guid tenantId, Guid messageId, int attempts, TimeSpan delay, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<InboundForAutomation?> ReadInboundAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Guid?> FindLastInboundIdAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> PurgeProcessedBeforeAsync(DateTimeOffset cutoff, int batch, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void EnqueueManualStart(Guid tenantId, Guid connectionId, Guid conversationId, DateTimeOffset now) =>
            throw new NotSupportedException();

        public Task<bool> HasPendingManualStartAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void EnqueueDelegated(Guid tenantId, Guid connectionId, Guid conversationId, Guid delegationId, DateTimeOffset now) =>
            throw new NotSupportedException();

        public void EnqueueAgentReturned(Guid tenantId, Guid connectionId, Guid conversationId, Guid delegationId, DateTimeOffset now) =>
            throw new NotSupportedException();

        public Task<DateTimeOffset?> ReassignPendingEngineAsync(Guid tenantId, Guid conversationId, BotKind target, Guid? exceptMessageId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
