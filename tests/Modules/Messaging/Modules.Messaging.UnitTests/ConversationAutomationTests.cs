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

    [Fact]
    public void SetAutomationStepNeedsAnAssistant()
    {
        Assert.Equal(MessagingErrorCodes.AutomationNotAvailable, Assert.Throws<MessagingDomainException>(() => Open().SetAutomationStep("Paso")).Code);
    }

    [Fact]
    public void SetAutomationStepTruncatesTo120AndLeavesVersionAndUpdatedAt()
    {
        var conversation = Open();
        conversation.StartAutomation(ChatbotId, Now);
        var version = conversation.Version;
        var updatedAt = conversation.UpdatedAt;

        conversation.SetAutomationStep(new string('a', 200));

        Assert.Equal(120, conversation.AutomationStep!.Length);
        Assert.Equal(version, conversation.Version);
        Assert.Equal(updatedAt, conversation.UpdatedAt);
    }

    [Fact]
    public void StartAndEndBumpVersionAndUpdatedAt()
    {
        var later = Now.AddMinutes(5);
        var conversation = Open();
        var version = conversation.Version;

        conversation.StartAutomation(ChatbotId, later);
        Assert.Equal((version + 1, later), (conversation.Version, conversation.UpdatedAt));

        conversation.EndAutomation(later.AddMinutes(1));
        Assert.Equal((version + 2, later.AddMinutes(1)), (conversation.Version, conversation.UpdatedAt));
    }

    [Fact]
    public void PausingKeepsChatbotAndStep()
    {
        var conversation = Open();
        conversation.StartAutomation(ChatbotId, Now);
        conversation.SetAutomationStep("Número de documento");

        conversation.PauseAutomation(null, AutomationPauseReason.Manual, Now);

        Assert.Equal(ChatbotId, conversation.AutomationChatbotId);
        Assert.Equal("Número de documento", conversation.AutomationStep);
    }

    private static readonly AutomationHandoff Cause = AutomationHandoff.Create(
        AutomationHandoffKind.Block, null, "handoff", "Pasar a un asesor", null, null,
        new AutomationHandoffHttp("policy-lookup", "Buscar cliente por teléfono", "error_status", "http_404", 404, "API de Clasic"));

    // Spec 2026-10-24 D-T7: la causa viaja en el mismo evento AutomationPaused, no en uno nuevo.
    [Fact]
    public void AHandoffPauseCarriesItsCauseInTheEvent()
    {
        var conversation = Open();
        conversation.StartAutomation(ChatbotId, Now);

        var change = conversation.PauseAutomation(null, AutomationPauseReason.Handoff, Now, Cause);

        Assert.Equal(ConversationEventType.AutomationPaused, change.Type);
        Assert.Equal(AutomationPauseReason.Handoff, change.PauseReason);
        Assert.Equal(Cause, change.Handoff);
        Assert.Equal(ConversationAutomationStatus.Paused, conversation.AutomationStatus);
    }

    [Fact]
    public void ACauseWithAnotherReasonThrows()
    {
        var conversation = Open();
        conversation.StartAutomation(ChatbotId, Now);
        var version = conversation.Version;

        Assert.Throws<ArgumentException>(() =>
            conversation.PauseAutomation(Guid.CreateVersion7(), AutomationPauseReason.Manual, Now.AddMinutes(1), Cause));

        Assert.Equal(ConversationAutomationStatus.Active, conversation.AutomationStatus);
        Assert.Equal(version, conversation.Version);
    }

    [Fact]
    public void APauseWithoutCauseStaysAsToday()
    {
        var conversation = Open();
        conversation.StartAutomation(ChatbotId, Now);

        var change = conversation.PauseAutomation(null, AutomationPauseReason.Handoff, Now);

        Assert.Null(change.Handoff);
        Assert.Equal(new ConversationEvent(ConversationEventType.AutomationPaused, PauseReason: AutomationPauseReason.Handoff), change);
    }

    // Spec 2026-10-25 D-1, D-7, D-8: el traspaso del sistema asigna sin actor y sin evento «Transferred».
    [Fact]
    public void AHandoffToAMemberPausesAndAssignsWithoutActor()
    {
        var conversation = Open();
        conversation.StartAutomation(ChatbotId, Now);
        var member = Guid.CreateVersion7();
        var handoff = AutomationHandoff.Create(AutomationHandoffKind.AttemptsExhausted, null, "q", "Documento", null, null, null, member);
        var at = Now.AddMinutes(3);

        var change = conversation.HandOffAutomation(handoff, at);

        Assert.Equal(ConversationAutomationStatus.Paused, conversation.AutomationStatus);
        Assert.Equal(AutomationPauseReason.Handoff, conversation.AutomationPauseReason);
        Assert.Null(conversation.AutomationPausedByMemberId);
        Assert.Equal(member, conversation.AssignedMemberId);
        Assert.Equal(at, conversation.AssignedAt);
        Assert.Equal(ConversationEventType.AutomationPaused, change.Type);
        Assert.Null(change.Actor);
        Assert.Equal(member, change.Handoff!.AssignedMemberId);
    }

    [Fact]
    public void AnUnassignedHandoffLeavesNoAssignee()
    {
        var conversation = Open();
        conversation.StartAutomation(ChatbotId, Now);

        var change = conversation.HandOffAutomation(Cause, Now);

        Assert.Null(conversation.AssignedMemberId);
        Assert.Equal(new ConversationEvent(ConversationEventType.AutomationPaused, PauseReason: AutomationPauseReason.Handoff, Handoff: Cause), change);
    }

    [Fact]
    public void AHandoffNeedsAnOpenConversationWithAnActiveAssistant()
    {
        var member = Guid.CreateVersion7();
        var handoff = AutomationHandoff.Create(AutomationHandoffKind.AttemptsExhausted, null, null, null, null, null, null, member);

        var paused = Open();
        paused.StartAutomation(ChatbotId, Now);
        paused.PauseAutomation(null, AutomationPauseReason.Manual, Now);
        var none = Open();
        var resolved = Open();
        resolved.StartAutomation(ChatbotId, Now);
        resolved.Resolve(Now);

        foreach (var (conversation, code) in new[]
        {
            (paused, MessagingErrorCodes.AutomationAlreadyPaused),
            (none, MessagingErrorCodes.AutomationNotAvailable),
            (resolved, MessagingErrorCodes.ConversationNotOpen),
        })
        {
            var version = conversation.Version;
            var error = Assert.Throws<MessagingDomainException>(() => conversation.HandOffAutomation(handoff, Now.AddMinutes(1)));

            Assert.Equal(code, error.Code);
            Assert.Null(conversation.AssignedMemberId);
            Assert.Equal(version, conversation.Version);
        }
    }
}
