using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-11-17 (agente IA en chatbots) §5.3: el chatbot delega la conversación en un agente y la recupera sin pasar por
/// <c>None</c>, y toda transición que la saca de <c>Active</c> limpia la delegación (si no, <c>CK_conversations_delegation</c> falla).</summary>
public sealed class ConversationDelegationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid ChatbotId = Guid.CreateVersion7();
    private static readonly Guid AgentId = Guid.CreateVersion7();
    private static readonly Guid DelegationId = Guid.CreateVersion7();
    private static readonly Guid Member = Guid.CreateVersion7();

    private static Conversation Open() =>
        Conversation.ForTests(
            Conversation.Start(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "573001234567", "Laura", Now),
            unreadCount: 0, lastInboundAt: Now);

    private static Conversation WithChatbot()
    {
        var conversation = Open();
        conversation.StartAutomation(ChatbotId, Now);
        conversation.SetAutomationStep("Menú");
        return conversation;
    }

    private static Conversation Delegated()
    {
        var conversation = WithChatbot();
        conversation.DelegateToAgent(DelegationId, AgentId, Now.AddMinutes(1));
        return conversation;
    }

    [Fact]
    public void DelegatingKeepsTheConversationActiveAndChangesTheBotWithoutPassingThroughNone()
    {
        var conversation = WithChatbot();
        var version = conversation.Version;

        var change = conversation.DelegateToAgent(DelegationId, AgentId, Now.AddMinutes(1));

        Assert.Equal(ConversationAutomationStatus.Active, conversation.AutomationStatus);
        Assert.Equal(BotKind.Agent, conversation.AutomationBotKind);
        Assert.Equal(AgentId, conversation.AutomationAgentId);
        // CK_conversations_automation_bot: con un agente no hay chatbot; el de la delegación vive en messaging.bot_delegations.
        Assert.Null(conversation.AutomationChatbotId);
        Assert.Null(conversation.AutomationStep);
        Assert.Equal(DelegationId, conversation.AutomationDelegationId);
        Assert.Equal(version + 1, conversation.Version);
        Assert.Equal(new ConversationEvent(ConversationEventType.AutomationDelegated, ChatbotId: ChatbotId), change);
    }

    [Fact]
    public void DelegatingNeverTouchesStartedAtNorResumedAt()
    {
        var conversation = WithChatbot();
        var started = conversation.AutomationStartedAt;

        conversation.DelegateToAgent(DelegationId, AgentId, Now.AddMinutes(5));
        conversation.ReturnFromAgent(DelegationId, ChatbotId, Now.AddMinutes(9));

        // BotAttendance.HumanTookOver compara AutomationResumedAt con el arranque de la corrida: sellarlo la daría por interrumpida.
        Assert.Equal(started, conversation.AutomationStartedAt);
        Assert.Null(conversation.AutomationResumedAt);
    }

    public static TheoryData<string, string> NotDelegable => new()
    {
        { "none", MessagingErrorCodes.AutomationNotAvailable },
        { "paused", MessagingErrorCodes.AutomationNotAvailable },
        { "agent", MessagingErrorCodes.AutomationNotAvailable },
        { "delegated", MessagingErrorCodes.AutomationNotAvailable },
        { "advisor", MessagingErrorCodes.AutomationNotAvailable },
        { "resolved", MessagingErrorCodes.ConversationNotOpen },
    };

    [Theory]
    [MemberData(nameof(NotDelegable))]
    public void DelegatingRequiresAnActiveChatbotConversationWithoutAdvisor(string state, string code)
    {
        var conversation = Open();
        switch (state)
        {
            case "paused":
                conversation.StartAutomation(ChatbotId, Now);
                conversation.PauseAutomation(Member, AutomationPauseReason.Manual, Now);
                break;
            case "agent":
                conversation.StartAutomation(BotRef.Agent(AgentId), Now);
                break;
            case "delegated":
                conversation = Delegated();
                break;
            case "advisor":
                conversation.StartAutomation(ChatbotId, Now);
                conversation.Take(Member, Now);
                break;
            case "resolved":
                conversation.StartAutomation(ChatbotId, Now);
                conversation.Resolve(Now);
                break;
        }

        var version = conversation.Version;
        var error = Assert.Throws<MessagingDomainException>(() => conversation.DelegateToAgent(Guid.CreateVersion7(), AgentId, Now.AddMinutes(2)));

        Assert.Equal(code, error.Code);
        Assert.Equal(version, conversation.Version);
    }

    [Fact]
    public void ReturningRestoresTheChatbotAndClearsTheDelegation()
    {
        var conversation = Delegated();
        var version = conversation.Version;

        var change = conversation.ReturnFromAgent(DelegationId, ChatbotId, Now.AddMinutes(3));

        Assert.Equal(ConversationAutomationStatus.Active, conversation.AutomationStatus);
        Assert.Equal(BotKind.Chatbot, conversation.AutomationBotKind);
        Assert.Equal(ChatbotId, conversation.AutomationChatbotId);
        Assert.Null(conversation.AutomationAgentId);
        Assert.Null(conversation.AutomationDelegationId);
        Assert.Equal(version + 1, conversation.Version);
        Assert.Equal(new ConversationEvent(ConversationEventType.AutomationReturned, ChatbotId: ChatbotId), change);
    }

    [Fact]
    public void ReturningAnotherDelegationIsRejected()
    {
        var conversation = Delegated();
        var version = conversation.Version;

        var error = Assert.Throws<MessagingDomainException>(() => conversation.ReturnFromAgent(Guid.CreateVersion7(), ChatbotId, Now.AddMinutes(3)));

        Assert.Equal(MessagingErrorCodes.DelegationNotLive, error.Code);
        Assert.Equal("messaging.automation.delegation_not_live", MessagingErrorCodes.DelegationNotLive);
        Assert.Equal(DelegationId, conversation.AutomationDelegationId);
        Assert.Equal(version, conversation.Version);
    }

    [Fact]
    public void ReturningAConversationWithoutDelegationIsRejected()
    {
        var conversation = WithChatbot();

        var error = Assert.Throws<MessagingDomainException>(() => conversation.ReturnFromAgent(DelegationId, ChatbotId, Now));

        Assert.Equal(MessagingErrorCodes.DelegationNotLive, error.Code);
    }

    [Theory]
    [InlineData("take")]
    [InlineData("resolve")]
    [InlineData("pause")]
    [InlineData("transfer")]
    [InlineData("end")]
    [InlineData("handoff")]
    [InlineData("startByMember")]
    public void TakingResolvingPausingTransferringOrEndingClearsTheDelegation(string transition)
    {
        var conversation = Delegated();
        var later = Now.AddMinutes(4);

        switch (transition)
        {
            case "take":
                conversation.Take(Member, later);
                break;
            case "resolve":
                conversation.Resolve(later);
                break;
            case "pause":
                conversation.PauseAutomation(Member, AutomationPauseReason.Manual, later);
                break;
            case "transfer":
                conversation.TransferTo(Member, Guid.CreateVersion7(), later);
                break;
            case "end":
                conversation.EndAutomation(later);
                break;
            case "handoff":
                conversation.HandOffAutomation(AutomationHandoff.Create(AutomationHandoffKind.AgentRequested, "customer_asked", null, null, null, null, null), later);
                break;
            case "startByMember":
                conversation.StartAutomationByMember(Member, ChatbotId, later);
                break;
        }

        // CK_conversations_delegation: la delegación sólo existe con el asistente Active y bot_kind = 2.
        Assert.Null(conversation.AutomationDelegationId);
    }
}
