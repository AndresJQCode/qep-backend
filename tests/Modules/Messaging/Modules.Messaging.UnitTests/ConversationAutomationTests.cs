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

    private static readonly Guid Member = Guid.CreateVersion7();

    /// <summary>Una conversación de WhatsApp abierta, sin asesor, con un entrante de hace una hora: la ventana está abierta.</summary>
    private static Conversation WithOpenWindow() => Conversation.ForTests(Open(), unreadCount: 0, lastInboundAt: Now.AddHours(-1));

    // Spec 2026-10-31: un asesor arranca el asistente desde el hilo y eso borra cualquier pausa, también las deliberadas.
    [Theory]
    [InlineData(AutomationPauseReason.Manual)]
    [InlineData(AutomationPauseReason.Handoff)]
    [InlineData(AutomationPauseReason.AdvisorTook)]
    [InlineData(AutomationPauseReason.AdvisorReplied)]
    [InlineData(AutomationPauseReason.AdvisorTransferred)]
    public void StartByMemberClearsAnyPause(AutomationPauseReason reason)
    {
        var conversation = WithOpenWindow();
        conversation.StartAutomation(ChatbotId, Now.AddMinutes(-30));
        conversation.SetAutomationStep("Número de documento");
        conversation.PauseAutomation(Member, reason, Now.AddMinutes(-20));
        var version = conversation.Version;
        var at = Now.AddMinutes(1);

        var change = conversation.StartAutomationByMember(Member, ChatbotId, at);

        Assert.Equal(ConversationAutomationStatus.Active, conversation.AutomationStatus);
        Assert.Equal(ChatbotId, conversation.AutomationChatbotId);
        Assert.Equal(at, conversation.AutomationStartedAt);
        Assert.Equal(at, conversation.AutomationResumedAt);
        Assert.Null(conversation.AutomationPausedAt);
        Assert.Null(conversation.AutomationPausedByMemberId);
        Assert.Null(conversation.AutomationPauseReason);
        Assert.Null(conversation.AutomationStep);
        Assert.False(conversation.HistoryManaged);
        Assert.Equal(new ConversationEvent(ConversationEventType.AutomationStarted, Actor: Member), change);
        Assert.Equal(version + 1, conversation.Version);
    }

    [Fact]
    public void StartByMemberWorksFromNoneAndFromActive()
    {
        var none = WithOpenWindow();
        var active = WithOpenWindow();
        active.StartAutomation(ChatbotId, Now.AddMinutes(-30));

        foreach (var conversation in new[] { none, active })
        {
            var change = conversation.StartAutomationByMember(Member, ChatbotId, Now);

            Assert.Equal(ConversationAutomationStatus.Active, conversation.AutomationStatus);
            Assert.Equal(Now, conversation.AutomationStartedAt);
            Assert.Equal(Now, conversation.AutomationResumedAt);
            Assert.Equal(ConversationEventType.AutomationStarted, change.Type);
        }
    }

    [Fact]
    public void StartByMemberRejectsAResolvedConversation()
    {
        var conversation = WithOpenWindow();
        conversation.Resolve(Now.AddMinutes(-5));

        var error = Assert.Throws<MessagingDomainException>(() => conversation.StartAutomationByMember(Member, ChatbotId, Now));

        Assert.Equal(MessagingErrorCodes.ConversationNotOpen, error.Code);
    }

    [Fact]
    public void StartByMemberRejectsAnAdvisor()
    {
        var conversation = WithOpenWindow();
        conversation.Take(Member, Now.AddMinutes(-5));
        var version = conversation.Version;

        var error = Assert.Throws<MessagingDomainException>(() => conversation.StartAutomationByMember(Member, ChatbotId, Now));

        Assert.Equal(MessagingErrorCodes.AutomationHasAdvisor, error.Code);
        Assert.Equal(version, conversation.Version);
    }

    [Fact]
    public void StartByMemberRejectsABlockedContact()
    {
        var conversation = WithOpenWindow();
        conversation.Block(Member, "573001234567", Now.AddMinutes(-5));

        var error = Assert.Throws<MessagingDomainException>(() => conversation.StartAutomationByMember(Member, ChatbotId, Now));

        Assert.Equal(MessagingErrorCodes.ContactBlocked, error.Code);
    }

    [Fact]
    public void StartByMemberRejectsInstagram()
    {
        var conversation = Conversation.ForTests(
            Conversation.StartWithUserId(
                Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "17841400000000000", null, "Laura", Now.AddDays(-2),
                ConversationChannel.Instagram),
            unreadCount: 0,
            lastInboundAt: Now.AddHours(-1));

        var error = Assert.Throws<MessagingDomainException>(() => conversation.StartAutomationByMember(Member, ChatbotId, Now));

        Assert.Equal(MessagingErrorCodes.ChannelUnsupported, error.Code);
    }

    [Fact]
    public void StartByMemberRejectsAClosedWindow()
    {
        var expired = Conversation.ForTests(Open(), unreadCount: 0, lastInboundAt: Now.AddHours(-25));
        var neverInbound = Open();

        foreach (var conversation in new[] { expired, neverInbound })
        {
            var version = conversation.Version;
            var error = Assert.Throws<MessagingDomainException>(() => conversation.StartAutomationByMember(Member, ChatbotId, Now));

            Assert.Equal(MessagingErrorCodes.WindowClosed, error.Code);
            Assert.Equal(version, conversation.Version);
        }
    }

    // La orden del motor sigue sin reactivar lo que una persona pausó a propósito: eso es de StartAutomationByMember y ResumeAutomation.
    [Theory]
    [InlineData(AutomationPauseReason.Manual)]
    [InlineData(AutomationPauseReason.Handoff)]
    public void StartAutomationKeepsRejectingDeliberatePauses(AutomationPauseReason reason)
    {
        var conversation = Open();
        conversation.StartAutomation(ChatbotId, Now);
        conversation.PauseAutomation(null, reason, Now);

        var error = Assert.Throws<MessagingDomainException>(() => conversation.StartAutomation(ChatbotId, Now.AddMinutes(1)));

        Assert.Equal(MessagingErrorCodes.AutomationNotAvailable, error.Code);
    }

    // Spec 2026-11-01: continuar donde iba. Es la pausa manual sin asesor y nada más; no sella AutomationResumedAt, y esa ausencia es el
    // marcador: BotAttendance.HumanTookOver da falso y el siguiente entrante sigue la corrida viva.
    private static Conversation PausedByHand(DateTimeOffset pausedAt)
    {
        var conversation = WithOpenWindow();
        conversation.StartAutomation(ChatbotId, Now.AddMinutes(-30));
        conversation.SetAutomationStep("Número de documento");
        conversation.PauseAutomation(Member, AutomationPauseReason.Manual, pausedAt);
        return conversation;
    }

    [Fact]
    public void ContinueClearsThePauseAndKeepsTheFlowWithoutAStamp()
    {
        var conversation = PausedByHand(Now.AddMinutes(-20));
        var startedAt = conversation.AutomationStartedAt;
        var version = conversation.Version;
        var at = Now.AddMinutes(1);

        var change = conversation.ContinueAutomation(Member, at);

        Assert.Equal(ConversationAutomationStatus.Active, conversation.AutomationStatus);
        Assert.Null(conversation.AutomationPausedAt);
        Assert.Null(conversation.AutomationPausedByMemberId);
        Assert.Null(conversation.AutomationPauseReason);
        Assert.Null(conversation.AutomationResumedAt);
        Assert.Equal(startedAt, conversation.AutomationStartedAt);
        Assert.Equal(ChatbotId, conversation.AutomationChatbotId);
        Assert.Equal("Número de documento", conversation.AutomationStep);
        Assert.Equal(new ConversationEvent(ConversationEventType.AutomationContinued, Actor: Member), change);
        Assert.Equal(version + 1, conversation.Version);
    }

    /// <summary>Un regreso anterior se conserva tal cual: continuar no lo mueve ni lo borra.</summary>
    [Fact]
    public void ContinueKeepsAnEarlierResumeStamp()
    {
        var conversation = PausedByHand(Now.AddMinutes(-20));
        var resumedAt = Now.AddMinutes(-10);
        conversation.ResumeAutomation(Member, resumedAt);
        conversation.PauseAutomation(Member, AutomationPauseReason.Manual, Now.AddMinutes(-5));

        conversation.ContinueAutomation(Member, Now);

        Assert.Equal(resumedAt, conversation.AutomationResumedAt);
    }

    [Theory]
    [InlineData(AutomationPauseReason.Handoff)]
    [InlineData(AutomationPauseReason.AdvisorTook)]
    [InlineData(AutomationPauseReason.AdvisorReplied)]
    [InlineData(AutomationPauseReason.AdvisorTransferred)]
    public void ContinueRejectsEveryOtherPauseReason(AutomationPauseReason reason)
    {
        var conversation = WithOpenWindow();
        conversation.StartAutomation(ChatbotId, Now.AddMinutes(-30));
        conversation.PauseAutomation(null, reason, Now.AddMinutes(-20));
        var version = conversation.Version;

        var error = Assert.Throws<MessagingDomainException>(() => conversation.ContinueAutomation(Member, Now));

        Assert.Equal(MessagingErrorCodes.AutomationCannotContinue, error.Code);
        Assert.Equal(ConversationAutomationStatus.Paused, conversation.AutomationStatus);
        Assert.Equal(version, conversation.Version);
    }

    [Fact]
    public void ContinueRejectsActiveNoneAdvisorAndResolved()
    {
        var active = WithOpenWindow();
        active.StartAutomation(ChatbotId, Now);
        var withAdvisor = PausedByHand(Now.AddMinutes(-20));
        withAdvisor.Take(Member, Now.AddMinutes(-10));
        var resolved = PausedByHand(Now.AddMinutes(-20));
        resolved.Resolve(Now.AddMinutes(-10));

        Assert.Equal(MessagingErrorCodes.AutomationAlreadyActive, Assert.Throws<MessagingDomainException>(() => active.ContinueAutomation(Member, Now)).Code);
        Assert.Equal(MessagingErrorCodes.AutomationNotAvailable, Assert.Throws<MessagingDomainException>(() => WithOpenWindow().ContinueAutomation(Member, Now)).Code);
        Assert.Equal(MessagingErrorCodes.AutomationHasAdvisor, Assert.Throws<MessagingDomainException>(() => withAdvisor.ContinueAutomation(Member, Now)).Code);
        Assert.Equal(MessagingErrorCodes.ConversationNotOpen, Assert.Throws<MessagingDomainException>(() => resolved.ContinueAutomation(Member, Now)).Code);
    }
}
