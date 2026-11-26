namespace Modules.Support.Domain;

/// <summary>Los eventos de la traza: los de la fase 1 (plan D9), los de Mensajería de la fase 2 (plan E7) la prórroga de la fase
/// 4a, el cambio de prioridad (spec 2026-11-15) y el PIN nuevo de un anónimo (spec 2026-11-16). Se persisten por nombre: un valor
/// nuevo va al final, y como el CHECK <c>CK_ticket_events_kind</c> sale de esta lista, cada valor nuevo trae su migración. Un vínculo
/// automático de un entrante no escribe evento: el registro es la fila del vínculo.</summary>
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
    PriorityChanged,
    PinReissued,

    /// <summary>Spec 2026-11-22: cambió la fecha límite libre de una tarea interna.</summary>
    DueDateChanged,
}
