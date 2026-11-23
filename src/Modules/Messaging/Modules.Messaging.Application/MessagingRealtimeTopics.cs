namespace Modules.Messaging.Application;

/// <summary>Spec 2026-11-22 §9.2: Messaging's topic on the app hub. <c>messaging</c> is the tenant-wide alert for an inbound message that
/// nobody has taken yet; the exact text is the only valid form. It is contract with the screens, like the event name.</summary>
public static class MessagingRealtimeTopics
{
    public const string Prefix = "messaging";

    public const string Inbox = "messaging";
}

public static class MessagingRealtimeEvents
{
    /// <summary>Ids only (<c>conversationId</c>, <c>messageId</c>): the text of the message never leaves the server through a notice.</summary>
    public const string InboundMessageAlert = "inboundMessageAlert";
}
