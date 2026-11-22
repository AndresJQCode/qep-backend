using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-11-22 §6 and D-10: which conversation events tell a member that a conversation is now theirs.</summary>
public sealed class ConversationAssignmentNoticeTests
{
    private static readonly Guid Actor = Guid.CreateVersion7();
    private static readonly Guid Target = Guid.CreateVersion7();

    [Fact]
    public void ATransferToSomeoneElseNotifiesTheTarget()
    {
        var notice = ConversationAssignmentNotice.From(new ConversationEvent(ConversationEventType.Transferred, Actor: Actor, Target: Target));

        Assert.Equal(new AssignmentNotice(Target, "Transferred", Actor), notice);
    }

    [Fact]
    public void ATransferToOneselfDoesNotNotify() =>
        Assert.Null(ConversationAssignmentNotice.From(new ConversationEvent(ConversationEventType.Transferred, Actor: Actor, Target: Actor)));

    [Theory]
    [InlineData(ConversationEventType.Taken)]
    [InlineData(ConversationEventType.AutoTaken)]
    public void TakenAndAutoTakenDoNotNotify(ConversationEventType type) =>
        // Whoever receives the conversation is whoever acted, even when the event carries a target.
        Assert.Null(ConversationAssignmentNotice.From(new ConversationEvent(type, Actor: Actor, Target: Target)));

    [Fact]
    public void InheritedNotifiesTheHeir()
    {
        var notice = ConversationAssignmentNotice.From(new ConversationEvent(ConversationEventType.Inherited, Target: Target));

        // Nobody acted: the system gave it to the customer's assigned member.
        Assert.Equal(new AssignmentNotice(Target, "Inherited", null), notice);
    }

    [Fact]
    public void AHandoffWithAMemberNotifiesThatMember()
    {
        var handoff = AutomationHandoff.Create(AutomationHandoffKind.Block, null, null, null, null, null, null, assignedMemberId: Target);

        var notice = ConversationAssignmentNotice.From(new ConversationEvent(
            ConversationEventType.AutomationPaused, PauseReason: AutomationPauseReason.Handoff, Handoff: handoff));

        Assert.Equal(new AssignmentNotice(Target, "Handoff", null), notice);
    }

    [Fact]
    public void AHandoffWithoutMemberDoesNotNotify()
    {
        var handoff = AutomationHandoff.Create(AutomationHandoffKind.Block, null, null, null, null, null, null);

        Assert.Null(ConversationAssignmentNotice.From(new ConversationEvent(
            ConversationEventType.AutomationPaused, PauseReason: AutomationPauseReason.Handoff, Handoff: handoff)));
        // A pause that is not a handoff never carries an assignment, even with a stray target.
        Assert.Null(ConversationAssignmentNotice.From(new ConversationEvent(
            ConversationEventType.AutomationPaused, Actor: Actor, Target: Target, PauseReason: AutomationPauseReason.AdvisorReplied)));
    }
}
