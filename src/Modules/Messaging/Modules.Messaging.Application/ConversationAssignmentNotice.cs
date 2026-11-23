using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>Spec 2026-11-22 §6: who a conversation was just handed to, and why. <paramref name="Reason"/> is one of
/// <c>Transferred</c>, <c>Inherited</c> or <c>Handoff</c>; <paramref name="ActorMemberId"/> is the member who transferred it and is
/// <c>null</c> when the system or an assistant did.</summary>
public sealed record AssignmentNotice(Guid MemberId, string Reason, Guid? ActorMemberId);

/// <summary>
/// Spec 2026-11-22 D-10: the bell notice of an assignment is derived from the <see cref="ConversationEvent"/> in the two places where
/// that row is written (<c>ConversationEvents.Record</c> and <c>ConversationEventRows.InsertAsync</c>), so every path that assigns
/// (a transfer, a heritage on ingestion, an assistant's handoff) and any future one is covered, in the same transaction as the change.
/// It is a pure function: the decision lives here and the writers only persist it.
/// </summary>
public static class ConversationAssignmentNotice
{
    public const string EventName = "messaging.conversation.assigned.v1";

    public const string Transferred = "Transferred";

    public const string Inherited = "Inherited";

    public const string Handoff = "Handoff";

    /// <returns>The notice, or <c>null</c> when the event assigns nobody who needs to hear about it. <c>Taken</c> and <c>AutoTaken</c>
    /// never notify (whoever receives the conversation is whoever acted) and neither does a transfer to oneself.</returns>
    public static AssignmentNotice? From(ConversationEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Type switch
        {
            ConversationEventType.Transferred when value.Target is { } target && target != value.Actor =>
                new AssignmentNotice(target, Transferred, value.Actor),
            ConversationEventType.Inherited when value.Target is { } heir =>
                new AssignmentNotice(heir, Inherited, null),
            ConversationEventType.AutomationPaused when value.PauseReason == AutomationPauseReason.Handoff
                && value.Handoff?.AssignedMemberId is { } assigned =>
                new AssignmentNotice(assigned, Handoff, null),
            _ => null,
        };
    }
}
