namespace Modules.Messaging.Domain;

/// <summary>Los códigos del spec 2026-10-09 §10.1, en un solo lugar.</summary>
public static class MessagingErrorCodes
{
    public const string WindowClosed = "messaging.window_closed";
    public const string ConnectionUnavailable = "messaging.connection_unavailable";
    public const string ConversationNotOpen = "messaging.conversation.not_open";
    public const string MessageRejected = "messaging.message.rejected";
    public const string AlreadyResolved = "messaging.conversation.already_resolved";
    public const string AlreadyOpen = "messaging.conversation.already_open";
    public const string ConversationNotFound = "messaging.conversation.not_found";
    public const string MessageNotFound = "messaging.message.not_found";
    public const string WebhookSignatureInvalid = "messaging.webhook.signature_invalid";

    /// <summary>Spec 2026-10-10 §10: enviar a una conversación asignada a otra membresía.</summary>
    public const string AssignedToOther = "messaging.conversation.assigned_to_other";

    /// <summary>Spec 2026-10-10 §10: transferir a una membresía que no es activa del tenant o no tiene manage.</summary>
    public const string AssigneeCannotReply = "messaging.conversation.assignee_cannot_reply";
}
