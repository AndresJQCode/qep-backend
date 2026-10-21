using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-21 D-M11 y D-M14: el chatbot y el paso que viajan con la conversación, y cómo termina el asistente.</summary>
public sealed class ConversationAutomationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid ChatbotId = Guid.CreateVersion7();

    private static Conversation Open() =>
        Conversation.Start(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "573001234567", "Laura", Now);

    [Fact]
    public void StartAutomationRecordsTheChatbot()
    {
        var conversation = Open();

        conversation.StartAutomation(ChatbotId, Now);

        Assert.Equal(ChatbotId, conversation.AutomationChatbotId);
        Assert.Equal(ConversationAutomationStatus.Active, conversation.AutomationStatus);
    }

    [Fact]
    public void EndAutomationLeavesNoneAndClears()
    {
        var conversation = Open();
        conversation.StartAutomation(ChatbotId, Now);
        conversation.SetAutomationStep("Número de documento");

        conversation.EndAutomation(Now);

        Assert.Equal(ConversationAutomationStatus.None, conversation.AutomationStatus);
        Assert.Null(conversation.AutomationChatbotId);
        Assert.Null(conversation.AutomationStep);
        Assert.Null(conversation.AutomationStartedAt);
    }

    [Fact]
    public void EndAutomationRequiresActive()
    {
        var none = Open();
        var paused = Open();
        paused.StartAutomation(ChatbotId, Now);
        paused.PauseAutomation(null, AutomationPauseReason.Manual, Now);

        Assert.Equal(MessagingErrorCodes.AutomationNotAvailable, Assert.Throws<MessagingDomainException>(() => none.EndAutomation(Now)).Code);
        Assert.Equal(MessagingErrorCodes.AutomationNotAvailable, Assert.Throws<MessagingDomainException>(() => paused.EndAutomation(Now)).Code);
    }

    [Fact]
    public void ResolveClearsChatbotAndStep()
    {
        var conversation = Open();
        conversation.StartAutomation(ChatbotId, Now);
        conversation.SetAutomationStep("Número de documento");

        conversation.Resolve(Now);

        Assert.Null(conversation.AutomationChatbotId);
        Assert.Null(conversation.AutomationStep);
    }
}
