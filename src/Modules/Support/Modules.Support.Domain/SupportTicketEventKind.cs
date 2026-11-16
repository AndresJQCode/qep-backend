namespace Modules.Support.Domain;

/// <summary>Los eventos de la traza: los de la fase 1 (plan D9) y los de Mensajería de la fase 2 (plan E7). Se persisten por
/// nombre. Un vínculo automático de un entrante no escribe evento: el registro es la fila del vínculo.</summary>
public enum SupportTicketEventKind
{
    Created,
    StatusChanged,
    AssigneeChanged,
    TypeChanged,
    CustomerChanged,
    MessagePinned,
    MessageReleased,
    MessageClassified,
    MessageDismissed,
    FollowThreadChanged,
    ReplySent,
    ConversationLinked,
    PriorityChanged,
}
