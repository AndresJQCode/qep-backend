namespace Modules.Support.Domain;

/// <summary>Los eventos de la traza: los de la fase 1 (plan D9), los de Mensajería de la fase 2 (plan E7) y la prórroga de la fase
/// 4a. Se persisten por nombre: un valor nuevo va al final. Un vínculo automático de un entrante no escribe evento: el registro es la
/// fila del vínculo.</summary>
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
    Extended,
}
