using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-29 §4.4 y §6.5: la conversación sabe qué tipo de bot la atiende, la regla de salida es por canal y la causa de un
/// traspaso de agente lleva el proveedor.</summary>
public sealed class AgentAutomationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid AgentId = Guid.CreateVersion7();
    private static readonly Guid ChatbotId = Guid.CreateVersion7();

    private static Conversation Open() =>
        Conversation.Start(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "573001234567", "Laura", Now);

    [Fact]
    public void StartingAnAgentRecordsTheAgentAndNoChatbot()
    {
        var conversation = Open();

        conversation.StartAutomation(BotRef.Agent(AgentId), Now);

        Assert.Equal(ConversationAutomationStatus.Active, conversation.AutomationStatus);
        Assert.Equal(BotKind.Agent, conversation.AutomationBotKind);
        Assert.Equal(AgentId, conversation.AutomationAgentId);
        Assert.Null(conversation.AutomationChatbotId);
    }

    [Fact]
    public void StartingAChatbotRecordsItsKind()
    {
        var conversation = Open();

        conversation.StartAutomation(ChatbotId, Now);

        Assert.Equal(BotKind.Chatbot, conversation.AutomationBotKind);
        Assert.Equal(ChatbotId, conversation.AutomationChatbotId);
        Assert.Null(conversation.AutomationAgentId);
    }

    [Fact]
    public void AnAgentStartedAfterAnAdvisorPauseDropsTheOldChatbotAndStep()
    {
        var conversation = Open();
        conversation.StartAutomation(ChatbotId, Now);
        conversation.SetAutomationStep("Número de documento");
        conversation.PauseAutomation(null, AutomationPauseReason.AdvisorTook, Now);

        conversation.StartAutomation(BotRef.Agent(AgentId), Now.AddMinutes(1));

        Assert.Equal(BotKind.Agent, conversation.AutomationBotKind);
        Assert.Null(conversation.AutomationChatbotId);
        Assert.Null(conversation.AutomationStep);
    }

    [Fact]
    public void EndingTheAgentClearsTheBot()
    {
        var conversation = Open();
        conversation.StartAutomation(BotRef.Agent(AgentId), Now);

        conversation.EndAutomation(Now);

        Assert.Null(conversation.AutomationBotKind);
        Assert.Null(conversation.AutomationAgentId);
    }

    [Fact]
    public void ResolvingClearsTheAgent()
    {
        var conversation = Open();
        conversation.StartAutomation(BotRef.Agent(AgentId), Now);

        conversation.Resolve(Now);

        Assert.Null(conversation.AutomationBotKind);
        Assert.Null(conversation.AutomationAgentId);
    }

    [Fact]
    public void TheBotKindValuesAreTheStoredSmallints()
    {
        // connection_bots, conversations.automation_bot_kind y automation_inbox.bot_kind los guardan así.
        Assert.Equal(1, (int)BotKind.Chatbot);
        Assert.Equal(2, (int)BotKind.Agent);
        Assert.Equal(3, (int)MessageAuthorKind.Agent);
    }

    [Fact]
    public void EachChannelHasItsOwnTextLimitAndA24HourWindow()
    {
        var whatsApp = ChannelOutboundRules.For(ConversationChannel.WhatsApp);
        var instagram = ChannelOutboundRules.For(ConversationChannel.Instagram);

        Assert.Equal(TimeSpan.FromHours(24), whatsApp.Window);
        Assert.Equal(SendMessageValidator.TextMaxLength, whatsApp.TextMaxLength);
        Assert.Equal(Conversation.WindowLength, whatsApp.Window);
        Assert.Equal(TimeSpan.FromHours(24), instagram.Window);
        Assert.Equal(1000, instagram.TextMaxLength);
    }

    [Fact]
    public void TheWindowIsClosedWithoutAnInboundAndAfter24Hours()
    {
        Assert.False(ChannelOutboundRules.IsWindowOpen(ConversationChannel.Instagram, null, Now));
        Assert.True(ChannelOutboundRules.IsWindowOpen(ConversationChannel.Instagram, Now.AddHours(-23), Now));
        Assert.False(ChannelOutboundRules.IsWindowOpen(ConversationChannel.WhatsApp, Now.AddHours(-24), Now));
    }

    [Fact]
    public void AnAgentHandoffKeepsItsProvider()
    {
        var handoff = AutomationHandoff.Create(
            AutomationHandoffKind.ProviderFailed, "ai.provider.quota_exhausted", null, null, null, null, null, provider: "anthropic");

        Assert.Equal("anthropic", handoff.Provider);
        Assert.Equal("ai.provider.quota_exhausted", handoff.Code);
    }

    [Fact]
    public void AProviderThatIsNotACodeIsDropped() =>
        Assert.Null(AutomationHandoff.Create(AutomationHandoffKind.AgentFailed, null, null, null, null, null, null, provider: "Anthropic Claude").Provider);

    [Fact]
    public void TheProviderRoundTripsThroughTheEventJson()
    {
        var handoff = AutomationHandoff.Create(
            AutomationHandoffKind.AgentRequested, "customer_asked", null, null, null, null, null, provider: "openai");
        var json = ConversationEventJson.Serialize(new ConversationEvent(
            ConversationEventType.AutomationPaused, PauseReason: AutomationPauseReason.Handoff, Handoff: handoff));

        var parsed = ConversationEventJson.Parse(json);

        Assert.Equal(AutomationHandoffKind.AgentRequested, parsed!.Handoff!.Kind);
        Assert.Equal("openai", parsed.Handoff.Provider);
    }

    [Fact]
    public void AnOldHandoffReadsWithoutAProvider()
    {
        var json = ConversationEventJson.Serialize(new ConversationEvent(
            ConversationEventType.AutomationPaused, PauseReason: AutomationPauseReason.Handoff,
            Handoff: AutomationHandoff.Create(AutomationHandoffKind.Block, null, "n1", "Pasar", null, null, null)));

        Assert.DoesNotContain("provider", json, StringComparison.Ordinal);
        Assert.Null(ConversationEventJson.Parse(json)!.Handoff!.Provider);
    }
}
