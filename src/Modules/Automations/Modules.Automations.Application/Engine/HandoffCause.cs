namespace Modules.Automations.Application.Engine;

/// <summary>Spec 2026-10-24 §4.1: de qué camino salió un traspaso. Los nombres son los de <c>AutomationHandoffKind</c> de Mensajería, que
/// el adaptador traduce por nombre (<c>HandoffCauseKindParityTests</c>).</summary>
public enum HandoffCauseKind
{
    /// <summary>El flujo llegó a un bloque «Pasar a un asesor».</summary>
    Block,

    /// <summary>Botones o Lista con <c>fallback = handoff</c> y una respuesta inválida.</summary>
    InvalidReply,

    /// <summary>Un envío del bot que Mensajería rechazó.</summary>
    SendFailed,

    /// <summary>Un ciclo, un bloque inexistente o una versión borrada.</summary>
    FlowFailed,

    /// <summary>Spec 2026-10-25 D-1: se agotaron los reintentos de una pregunta.</summary>
    AttemptsExhausted,

    /// <summary>Spec 2026-10-26 repetir R-4c: Política de datos con «No acepto» al tope de <c>repeat</c>, o al primero en <c>handoff</c>.</summary>
    DataConsentRejected,

    /// <summary>Spec 2026-10-26 repetir R-4b: Política de datos en <c>repeat</c> o <c>handoff</c> sin cliente al que registrarle la respuesta.</summary>
    DataConsentUnavailable,
}

/// <summary>Spec 2026-10-24 D-T6: el bloque HTTP fallido que arrancó el paso que terminó en traspaso. Sólo códigos y la foto del título y
/// del nombre de la conexión: nada de la respuesta, los headers ni la clave (D-H5, D-H6, D-T3).</summary>
public sealed record HandoffHttpFailure(string NodeId, string? NodeTitle, string Outcome, string? ErrorCode, int? Status, string? ConnectionName);

/// <summary>Spec 2026-10-24 D-T6: por qué el motor pasó la conversación a un asesor. Viaja a Mensajería, que la guarda en el evento
/// <c>AutomationPaused</c> del hilo; la ve el tenant y nunca el cliente (D-T1).</summary>
/// <param name="Code">El <c>failure_code</c> de la corrida en <see cref="HandoffCauseKind.SendFailed"/> y
/// <see cref="HandoffCauseKind.FlowFailed"/>; <c>null</c> en <see cref="HandoffCauseKind.Block"/> e
/// <see cref="HandoffCauseKind.InvalidReply"/>.</param>
/// <param name="ConnectionName">Sólo en un envío rechazado: la conexión de WhatsApp.</param>
/// <param name="ConnectionReason">Sólo en un envío rechazado: el motivo de esa conexión (spec §3.4).</param>
public sealed record HandoffCause(
    HandoffCauseKind Kind,
    string? Code,
    string? NodeId,
    string? NodeTitle,
    string? ConnectionName,
    string? ConnectionReason,
    HandoffHttpFailure? Http)
{
    /// <summary>Un traspaso por falla del flujo, sin envío ni conexión de por medio.</summary>
    public static HandoffCause FlowFailed(string code, string? nodeId, string? nodeTitle, HandoffHttpFailure? http = null) =>
        new(HandoffCauseKind.FlowFailed, code, nodeId, nodeTitle, null, null, http);
}
