namespace Modules.Messaging.Domain;

/// <summary>Spec 2026-10-10 §6.1.5: qué pasó en la conversación. Se guarda por nombre en <c>details</c>.</summary>
public enum ConversationEventType
{
    Taken,
    Transferred,
    Released,
    AutoTaken,
    Inherited,
    Resolved,
    Reopened,
    CustomerCreated,
    CustomerLinked,
    ContactChangedNumber,
    AutomationPaused,
    AutomationResumed,
}

/// <summary>
/// Un evento del historial (spec 2026-10-10 §6.1.5): sólo ids de membresías y del cliente; los nombres se
/// resuelven al leer. <see cref="Actor"/> es <c>null</c> cuando lo hizo el sistema. <see cref="LinkedConversationId"/>
/// sólo lo usa <c>ContactChangedNumber</c> cuando el número nuevo ya tenía su conversación (P6). <see cref="ReasonId"/>
/// sólo lo usa <c>Resolved</c> (spec 2026-10-15): el id del motivo; el nombre se resuelve al leer. <see cref="PauseReason"/>
/// sólo lo usa <c>AutomationPaused</c> (spec 2026-10-21). Nunca teléfonos,
/// BSUIDs ni textos de Meta (§11).
/// </summary>
public sealed record ConversationEvent(
    ConversationEventType Type,
    Guid? Actor = null,
    Guid? Target = null,
    Guid? Previous = null,
    Guid? CustomerId = null,
    Guid? LinkedConversationId = null,
    Guid? ReasonId = null,
    AutomationPauseReason? PauseReason = null);
