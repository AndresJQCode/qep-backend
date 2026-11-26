using Modules.Ai.Domain;

namespace Modules.Ai.Application;

/// <summary>
/// Spec 2026-10-29 §4.4: de qué camino salió el traspaso de un agente. Es el espejo de los kinds de agente de <c>AutomationHandoffKind</c>
/// de Mensajería, que el adaptador de Bootstrapper traduce por nombre: Ai no referencia Messaging. Los amarra
/// <c>HandoffCauseKindParityTests</c>; si uno cambia sin el otro, la prueba se rompe antes que el <c>Enum.Parse</c> en producción.
/// </summary>
public enum HandoffCauseKind
{
    /// <summary>El modelo llamó <c>handoff_to_human</c> (el código es su motivo cerrado, <see cref="AgentHandoffReasons.All"/>), o una
    /// herramienta terminó el turno con un traspaso (P-4 y P-4b: <see cref="AgentHandoffCodes.QuotationDraftCreated"/>,
    /// <see cref="AgentHandoffCodes.QuotationCustomerNotReady"/>).</summary>
    AgentRequested,

    /// <summary>D-A21: llegó a <c>maxRepliesWithoutHuman</c>.</summary>
    AgentLimitReached,

    /// <summary>El agente falló: su respuesta, un tope de D-A20, el modelo o la petición.</summary>
    AgentFailed,

    /// <summary>La cuenta del proveedor falló (caída, tasa, saldo, clave, permiso o sin conexión).</summary>
    ProviderFailed,

    /// <summary>§6.8: el tenant apagó el módulo <c>ai</c>.</summary>
    ModuleDisabled,

    /// <summary>D-A18: la ventana del canal se cerró antes de enviar.</summary>
    WindowClosed,
}

/// <summary>Por qué un agente soltó la conversación. Viaja a Mensajería, que la guarda en el evento <c>AutomationPaused</c> del hilo; la ve el
/// tenant y nunca el cliente (D-T1). <see cref="Provider"/> siempre que hubo un agente con versión (plan 2026-11-12: «el evento interno del
/// traspaso lleva siempre el provider»).</summary>
/// <param name="ConnectionName">Sólo cuando Mensajería no pudo enviar por la conexión: su nombre.</param>
/// <param name="ConnectionReason">Sólo entonces: el motivo de la conexión (<c>paused</c>, <c>credentials_rejected</c>…).</param>
public sealed record AgentHandoffCause(
    HandoffCauseKind Kind, string? Code, string? Provider, string? ConnectionName = null, string? ConnectionReason = null);

/// <summary>
/// Los códigos de un traspaso de agente que no son errores del proveedor (los de la tabla «Errores del proveedor» del plan 2026-11-12 viven
/// en <see cref="AiModelErrorCodes"/>). El plan dejó a esta tarea decidir los de los topes de D-A20 y la respuesta inválida: son los mismos que
/// ya muestra la transcripción del playground, para que el tenant lea lo mismo en los dos lugares. Todos cumplen la regla de códigos del
/// hilo (<c>^[a-z0-9_.]{1,64}$</c>): uno que no la cumpla se guarda <c>null</c> sin error (<c>HandoffCauseKindParityTests</c>).
/// </summary>
public static class AgentHandoffCodes
{
    public const string InvalidResponse = "invalid_response";
    public const string ModelCallsExceeded = "model_calls_exceeded";
    public const string ToolCallsExceeded = "tool_calls_exceeded";
    public const string JobTimedOut = "job_timed_out";

    /// <summary>D-A21: llegó a <c>maxRepliesWithoutHuman</c>.</summary>
    public const string MaxRepliesWithoutHuman = "max_replies_without_human";

    /// <summary>Mensajería no pudo enviar la respuesta (conexión caída, mensaje rechazado).</summary>
    public const string SendFailed = "send_failed";

    /// <summary>P-4b: el agente creó el borrador de cotización y pasa la conversación a su asesora para que lo revise y lo envíe.</summary>
    public const string QuotationDraftCreated = "quotation_draft_created";

    /// <summary>P-4: el cliente de la conversación no existe o no se puede cotizar; no hay borrador.</summary>
    public const string QuotationCustomerNotReady = "quotation_customer_not_ready";

    /// <summary>El tenant pausó el agente con la sesión viva (owner, 2026-11-18): deja de contestar y la conversación pasa a una persona.</summary>
    public const string AgentPaused = "agent_paused";

    /// <summary>Spec 2026-11-23 D-3: cancelar o cambiar la hora fuera de las reglas (plazo, cita cerrada, servicio sin reserva por chat).</summary>
    public const string AppointmentChangeBlocked = "appointment_change_blocked";

    /// <summary>Spec 2026-11-23 D-6: no hay cliente al que agendarle (sin identidad en el canal, o inactivo).</summary>
    public const string AppointmentCustomerUnavailable = "appointment_customer_unavailable";

    /// <summary>Todos los códigos que un traspaso de agente puede llevar, para la prueba de paridad.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        InvalidResponse, ModelCallsExceeded, ToolCallsExceeded, JobTimedOut, MaxRepliesWithoutHuman, SendFailed, QuotationDraftCreated, AgentPaused,
        QuotationCustomerNotReady, AppointmentChangeBlocked, AppointmentCustomerUnavailable,
        AiModelErrorCodes.ProviderUnavailable, AiModelErrorCodes.RateLimited, AiModelErrorCodes.QuotaExhausted,
        AiModelErrorCodes.CredentialsRejected, AiModelErrorCodes.PermissionDenied, AiModelErrorCodes.ModelUnavailable,
        AiModelErrorCodes.RequestInvalid, AiModelErrorCodes.Refused, AiErrorCodes.ProviderNotConfigured,
        .. AgentHandoffReasons.All,
    ];

    /// <summary>El código de un tope o de la respuesta inválida.</summary>
    public static string For(AgentFailureReason reason) => reason switch
    {
        AgentFailureReason.InvalidResponse => InvalidResponse,
        AgentFailureReason.ModelCallsExceeded => ModelCallsExceeded,
        AgentFailureReason.ToolCallsExceeded => ToolCallsExceeded,
        AgentFailureReason.JobTimedOut => JobTimedOut,
        _ => AiModelErrorCodes.ProviderUnavailable,
    };
}
