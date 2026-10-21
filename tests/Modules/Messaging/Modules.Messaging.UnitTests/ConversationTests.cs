using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-09 §8.4–§8.5 y §8.7: resolver/reabrir con sus 422, cuándo procede el acuse de leído, y la ventana
/// de 24 h desde el último mensaje de la persona.</summary>
public sealed class ConversationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private static Conversation Open() =>
        Conversation.Start(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "573001234567", "Laura", Now);

    private static readonly Guid Andres = Guid.Parse("01900000-0000-7000-8000-0000000000b1");
    private static readonly Guid Beatriz = Guid.Parse("01900000-0000-7000-8000-0000000000b2");

    [Fact]
    public void StartIsOpenWithoutUnreadNorWindow()
    {
        var conversation = Open();

        Assert.Equal(ConversationStatus.Open, conversation.Status);
        Assert.Equal(0, conversation.UnreadCount);
        Assert.Null(conversation.LastInboundAt);
        Assert.Null(conversation.CustomerWindowExpiresAt);
        Assert.False(conversation.IsWindowOpen(Now));
        Assert.Equal(1, conversation.Version);
        Assert.Equal("573001234567", conversation.WaId);
    }

    [Fact]
    public void EnsureCanResolveRejectsAResolvedConversationAndTouchesNothingOnAnOpenOne()
    {
        var conversation = Open();
        var version = conversation.Version;

        conversation.EnsureCanResolve();
        Assert.Equal(version, conversation.Version);

        conversation.Resolve(Now.AddMinutes(1));
        var error = Assert.Throws<MessagingDomainException>(conversation.EnsureCanResolve);
        Assert.Equal(MessagingErrorCodes.AlreadyResolved, error.Code);
    }

    // Spec 2026-10-16 §3.2: resolver es la última palabra del asesor; la marca no sobrevive.
    [Fact]
    public void ResolveClearsTheUnreadCounter()
    {
        var conversation = Conversation.ForTests(Open(), unreadCount: 3, lastInboundAt: Now);

        conversation.Resolve(Now.AddMinutes(1));

        Assert.Equal(0, conversation.UnreadCount);
    }

    [Fact]
    public void ResolveAndReopenBumpTheVersionAndRejectANoOp()
    {
        var conversation = Open();

        conversation.Resolve(Now.AddMinutes(1));
        Assert.Equal(ConversationStatus.Resolved, conversation.Status);
        Assert.Equal(2, conversation.Version);
        Assert.Equal(Now.AddMinutes(1), conversation.UpdatedAt);

        var again = Assert.Throws<MessagingDomainException>(() => conversation.Resolve(Now.AddMinutes(2)));
        Assert.Equal(MessagingErrorCodes.AlreadyResolved, again.Code);
        Assert.Equal(2, conversation.Version);

        conversation.Reopen(Now.AddMinutes(3));
        Assert.Equal(ConversationStatus.Open, conversation.Status);
        Assert.Equal(3, conversation.Version);
        var open = Assert.Throws<MessagingDomainException>(() => conversation.Reopen(Now.AddMinutes(4)));
        Assert.Equal(MessagingErrorCodes.AlreadyOpen, open.Code);
    }

    // §8.4: marcar leído es un UPDATE atómico en Infrastructure; el dominio sólo decide si el acuse a Meta
    // procede con lo que ese UPDATE devuelve (wamid presente y último entrante de menos de 30 días).
    [Theory]
    [InlineData("wamid.in", -1.0, true)]
    [InlineData("wamid.in", -29.9, true)]
    [InlineData("wamid.in", -30.0, false)]
    [InlineData(null, -1.0, false)]
    public void TheReadReceiptNeedsAWamidAndAnInboundYoungerThanThirtyDays(string? wamid, double daysAgo, bool expected) =>
        Assert.Equal(expected, Conversation.CanAcknowledgeReading(wamid, Now.AddDays(daysAgo), Now));

    [Fact]
    public void TheReadReceiptNeedsAnInboundDate() =>
        Assert.False(Conversation.CanAcknowledgeReading("wamid.in", null, Now));

    [Theory]
    [InlineData(-23, true)]
    [InlineData(-24, false)]
    [InlineData(-25, false)]
    public void TheWindowIsTwentyFourHoursFromTheLastInbound(int hoursAgo, bool open)
    {
        var conversation = Conversation.ForTests(Open(), unreadCount: 0, lastInboundAt: Now.AddHours(hoursAgo));

        Assert.Equal(Now.AddHours(hoursAgo).AddHours(24), conversation.CustomerWindowExpiresAt);
        Assert.Equal(open, conversation.IsWindowOpen(Now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("+573001234567")]
    public void TheWaIdIsDigitsOnly(string? waId) =>
        Assert.Throws<ArgumentException>(() => Conversation.Start(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), waId!, null, Now));

    // Spec 2026-10-10 §6.1.1: ISO alfa-2 en mayúsculas + «.» + 1 a 128 alfanuméricos.
    [Theory]
    [InlineData("CO.1349120865530274", true)]
    [InlineData("US.13491208655302741918", true)]
    [InlineData("US.abcXYZ09", true)]
    [InlineData("co.1349", false)]
    [InlineData("COL.1349", false)]
    [InlineData("CO.", false)]
    [InlineData("CO.13-49", false)]
    [InlineData("573001234567", false)]
    [InlineData("CO.1349\n", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ABsuidHasTheShapeMetaDocuments(string? value, bool expected) =>
        Assert.Equal(expected, Conversation.IsValidUserId(value));

    [Fact]
    public void ABsuidOfMoreThan128AlphanumericsIsRejected()
    {
        Assert.True(Conversation.IsValidUserId("CO." + new string('9', 128)));
        Assert.False(Conversation.IsValidUserId("CO." + new string('9', 129)));
    }

    [Fact]
    public void ABsuidConversationMayHaveNoPhone()
    {
        var conversation = Conversation.StartWithUserId(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "CO.1349120865530274", null, "Laura", Now);

        Assert.Equal("CO.1349120865530274", conversation.UserId);
        Assert.Null(conversation.WaId);
        Assert.Null(conversation.AssignedMemberId);
        Assert.Null(conversation.CustomerId);
        Assert.Equal(1, conversation.Version);
    }

    [Theory]
    [InlineData("573001234567", "57300123456X")]
    [InlineData("co.1349", null)]
    public void AStartWithAMalformedIdentityIsRejected(string userId, string? waId) =>
        Assert.Throws<ArgumentException>(() =>
            Conversation.StartWithUserId(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), userId, waId, null, Now));

    [Fact]
    public void TakingAssignsBumpsTheVersionAndReturnsTheEventWithThePrevious()
    {
        var conversation = Open();

        var first = conversation.Take(Andres, Now.AddMinutes(1));
        var second = conversation.Take(Beatriz, Now.AddMinutes(2));

        Assert.Equal(new ConversationEvent(ConversationEventType.Taken, Actor: Andres), Assert.Single(first));
        Assert.Equal(new ConversationEvent(ConversationEventType.Taken, Actor: Beatriz, Previous: Andres), Assert.Single(second));
        Assert.Equal((Beatriz, Now.AddMinutes(2), 3L), (conversation.AssignedMemberId!.Value, conversation.AssignedAt!.Value, conversation.Version));
    }

    // D-A2: tomar la propia o liberar una sin asignar no cambia nada, ni la versión.
    [Fact]
    public void NoOpsReturnNoEventAndKeepTheVersion()
    {
        var conversation = Open();

        Assert.Null(conversation.Release(Andres, Now));
        conversation.Take(Andres, Now);
        Assert.Empty(conversation.Take(Andres, Now.AddMinutes(1)));
        Assert.Empty(conversation.TransferTo(Beatriz, Andres, Now.AddMinutes(1)));   // P8: ya es de Andrés
        Assert.Equal(2, conversation.Version);
    }

    [Fact]
    public void TransferringToYourselfIsATakeAndTransferringCarriesTargetAndPrevious()
    {
        var conversation = Open();
        conversation.Take(Andres, Now);

        var toSelf = conversation.TransferTo(Beatriz, Beatriz, Now.AddMinutes(1));
        var back = conversation.TransferTo(Beatriz, Andres, Now.AddMinutes(2));

        Assert.Equal(new ConversationEvent(ConversationEventType.Taken, Actor: Beatriz, Previous: Andres), Assert.Single(toSelf));
        Assert.Equal(new ConversationEvent(ConversationEventType.Transferred, Actor: Beatriz, Target: Andres, Previous: Beatriz), Assert.Single(back));
    }

    [Fact]
    public void ReleasingClearsTheAssignmentAndAResolvedOneCanStillBeTaken()
    {
        var conversation = Open();
        conversation.Resolve(Now);
        conversation.Take(Andres, Now.AddMinutes(1));

        var released = conversation.Release(Beatriz, Now.AddMinutes(2));

        Assert.Equal(new ConversationEvent(ConversationEventType.Released, Actor: Beatriz, Previous: Andres), released);
        Assert.Null(conversation.AssignedMemberId);
        Assert.Null(conversation.AssignedAt);
        Assert.Equal(ConversationStatus.Resolved, conversation.Status);
    }

    // Spec 2026-10-15 D-R2: la conversación guarda el motivo vigente y reabrir lo limpia.
    [Fact]
    public void ResolveKeepsTheReasonAndReopenClearsIt()
    {
        var conversation = Open();
        var reasonId = Guid.CreateVersion7();

        conversation.Resolve(Now.AddMinutes(1), reasonId);
        Assert.Equal(reasonId, conversation.ResolutionReasonId);

        conversation.Reopen(Now.AddMinutes(2));
        Assert.Null(conversation.ResolutionReasonId);

        conversation.Resolve(Now.AddMinutes(3));
        Assert.Null(conversation.ResolutionReasonId);
    }

    [Fact]
    public void AResolveThatFailsDoesNotTouchTheReason()
    {
        var conversation = Open();
        var first = Guid.CreateVersion7();
        conversation.Resolve(Now.AddMinutes(1), first);

        Assert.Throws<MessagingDomainException>(() => conversation.Resolve(Now.AddMinutes(2), Guid.CreateVersion7()));

        Assert.Equal(first, conversation.ResolutionReasonId);
    }

    private static Conversation Active()
    {
        var conversation = Open();
        conversation.StartAutomation(Now);
        return conversation;
    }

    private static string CodeOf(Action action) => Assert.Throws<MessagingDomainException>(action).Code;

    [Fact]
    public void StartAutomationNeedsAnOpenUnassignedConversationInNone()
    {
        var conversation = Open();
        conversation.StartAutomation(Now);
        Assert.Equal(ConversationAutomationStatus.Active, conversation.AutomationStatus);
        Assert.Equal(Now, conversation.AutomationStartedAt);
        Assert.Equal(2, conversation.Version);

        var assigned = Open();
        assigned.Take(Andres, Now);
        Assert.Equal(MessagingErrorCodes.AutomationNotAvailable, CodeOf(() => assigned.StartAutomation(Now)));

        var resolved = Open();
        resolved.Resolve(Now);
        Assert.Equal(MessagingErrorCodes.ConversationNotOpen, CodeOf(() => resolved.StartAutomation(Now)));

        Assert.Equal(MessagingErrorCodes.AutomationAlreadyActive, CodeOf(() => Active().StartAutomation(Now)));
    }

    [Fact]
    public void StartAutomationRejectsAPausedOne()
    {
        var conversation = Active();
        conversation.PauseAutomation(Andres, AutomationPauseReason.Manual, Now);
        Assert.Equal(MessagingErrorCodes.AutomationNotAvailable, CodeOf(() => conversation.StartAutomation(Now)));
    }

    [Fact]
    public void TakingAnActiveConversationPausesItWithAdvisorTook()
    {
        var conversation = Active();
        var t = Now.AddMinutes(3);

        var events = conversation.Take(Andres, t);

        Assert.Equal(
            [new ConversationEvent(ConversationEventType.Taken, Actor: Andres),
             new ConversationEvent(ConversationEventType.AutomationPaused, Actor: Andres, PauseReason: AutomationPauseReason.AdvisorTook)],
            events);
        Assert.Equal(ConversationAutomationStatus.Paused, conversation.AutomationStatus);
        Assert.Equal(Andres, conversation.AutomationPausedByMemberId);
        Assert.Equal(t, conversation.AutomationPausedAt);
        Assert.Equal(AutomationPauseReason.AdvisorTook, conversation.AutomationPauseReason);
    }

    [Fact]
    public void TransferringAnActiveConversationPausesItWithAdvisorTransferred()
    {
        var conversation = Active();

        var events = conversation.TransferTo(Andres, Beatriz, Now.AddMinutes(1));

        Assert.Equal(
            [new ConversationEvent(ConversationEventType.Transferred, Actor: Andres, Target: Beatriz),
             new ConversationEvent(ConversationEventType.AutomationPaused, Actor: Andres, PauseReason: AutomationPauseReason.AdvisorTransferred)],
            events);
    }

    // TransferTo(self, self) delega en Take: la razón es AdvisorTook, no AdvisorTransferred.
    [Fact]
    public void TransferringAnActiveConversationToYourselfPausesItWithAdvisorTook()
    {
        var conversation = Active();

        var events = conversation.TransferTo(Andres, Andres, Now.AddMinutes(1));

        Assert.Equal(
            [new ConversationEvent(ConversationEventType.Taken, Actor: Andres),
             new ConversationEvent(ConversationEventType.AutomationPaused, Actor: Andres, PauseReason: AutomationPauseReason.AdvisorTook)],
            events);
    }

    [Fact]
    public void PausingChecksOpenBeforeTheAutomationState()
    {
        var conversation = Active();
        conversation.Resolve(Now.AddMinutes(1));

        Assert.Equal(MessagingErrorCodes.ConversationNotOpen,
            CodeOf(() => conversation.PauseAutomation(Andres, AutomationPauseReason.Manual, Now.AddMinutes(2))));
    }

    [Fact]
    public void TakingANoneConversationReturnsOnlyTheTakenEvent()
    {
        var conversation = Open();

        Assert.Single(conversation.Take(Andres, Now));
        Assert.Empty(conversation.Take(Andres, Now));
        Assert.Equal(ConversationAutomationStatus.None, conversation.AutomationStatus);
    }

    [Fact]
    public void ReleasingLeavesTheAutomationPaused()
    {
        var conversation = Active();
        conversation.Take(Andres, Now);

        conversation.Release(Andres, Now.AddMinutes(1));

        Assert.Equal(ConversationAutomationStatus.Paused, conversation.AutomationStatus);
    }

    [Fact]
    public void ResolvingClearsTheAutomation()
    {
        var conversation = Active();
        conversation.Take(Andres, Now);

        conversation.Resolve(Now.AddMinutes(1));

        Assert.Equal(ConversationAutomationStatus.None, conversation.AutomationStatus);
        Assert.Null(conversation.AutomationStartedAt);
        Assert.Null(conversation.AutomationPausedAt);
        Assert.Null(conversation.AutomationPausedByMemberId);
        Assert.Null(conversation.AutomationPauseReason);
    }

    [Fact]
    public void ResumingWithAnAssigneeReleasesFirst()
    {
        var conversation = Active();
        conversation.Take(Andres, Now);

        var events = conversation.ResumeAutomation(Beatriz, Now.AddMinutes(1));

        Assert.Equal(
            [new ConversationEvent(ConversationEventType.Released, Actor: Beatriz, Previous: Andres),
             new ConversationEvent(ConversationEventType.AutomationResumed, Actor: Beatriz)],
            events);
        Assert.Null(conversation.AssignedMemberId);
        Assert.Equal(ConversationAutomationStatus.Active, conversation.AutomationStatus);
        Assert.Null(conversation.AutomationPausedAt);
    }

    [Fact]
    public void ResumingWithoutAnAssigneeReturnsOnlyTheResumedEvent()
    {
        var conversation = Active();
        conversation.PauseAutomation(Andres, AutomationPauseReason.Manual, Now);

        var events = conversation.ResumeAutomation(Beatriz, Now.AddMinutes(1));

        Assert.Equal(new ConversationEvent(ConversationEventType.AutomationResumed, Actor: Beatriz), Assert.Single(events));
    }

    [Fact]
    public void ResumingChecksOpenBeforeTheAutomationState()
    {
        var conversation = Active();
        conversation.Resolve(Now);

        Assert.Equal(MessagingErrorCodes.ConversationNotOpen, CodeOf(() => conversation.ResumeAutomation(Andres, Now)));
    }

    [Fact]
    public void ResumingRejectsNoneAndActive()
    {
        Assert.Equal(MessagingErrorCodes.AutomationNotAvailable, CodeOf(() => Open().ResumeAutomation(Andres, Now)));
        Assert.Equal(MessagingErrorCodes.AutomationAlreadyActive, CodeOf(() => Active().ResumeAutomation(Andres, Now)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PausingManuallyRejectsPausedAndNone(bool paused)
    {
        var conversation = paused ? Active() : Open();
        if (paused)
        {
            conversation.PauseAutomation(Andres, AutomationPauseReason.Manual, Now);
        }

        var expected = paused ? MessagingErrorCodes.AutomationAlreadyPaused : MessagingErrorCodes.AutomationNotAvailable;
        Assert.Equal(expected, CodeOf(() => conversation.PauseAutomation(Andres, AutomationPauseReason.Manual, Now)));
    }
}

/// <summary>Spec 2026-10-14 decisión 4: el canal es un dato de la conversación y decide la forma de <c>user_id</c>.</summary>
public sealed class ConversationChannelTests
{
    [Fact]
    public void AnInstagramConversationTakesTheNumericIgsidAndNoPhone()
    {
        var conversation = Modules.Messaging.Domain.Conversation.StartWithUserId(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "1234567890123456", null, "Laura", DateTimeOffset.UtcNow,
            Modules.Messaging.Domain.ConversationChannel.Instagram);

        Assert.Equal(Modules.Messaging.Domain.ConversationChannel.Instagram, conversation.Channel);
        Assert.Equal("1234567890123456", conversation.UserId);
        Assert.Null(conversation.WaId);
    }

    [Theory]
    [InlineData("CO.1349120865530274")]
    [InlineData("")]
    [InlineData("abc")]
    public void AnInstagramConversationRejectsANonNumericUserId(string userId) =>
        Assert.Throws<ArgumentException>(() => Modules.Messaging.Domain.Conversation.StartWithUserId(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), userId, null, "Laura", DateTimeOffset.UtcNow,
            Modules.Messaging.Domain.ConversationChannel.Instagram));

    [Fact]
    public void AWhatsAppConversationStillTakesABsuidAndDefaultsToWhatsApp()
    {
        var conversation = Modules.Messaging.Domain.Conversation.StartWithUserId(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "CO.1349120865530274", "573001234567", "Laura", DateTimeOffset.UtcNow);

        Assert.Equal(Modules.Messaging.Domain.ConversationChannel.WhatsApp, conversation.Channel);
        Assert.False(Modules.Messaging.Domain.Conversation.IsValidUserId("1234567890123456", Modules.Messaging.Domain.ConversationChannel.WhatsApp));
        Assert.True(Modules.Messaging.Domain.Conversation.IsValidUserId("1234567890123456", Modules.Messaging.Domain.ConversationChannel.Instagram));
    }
}
