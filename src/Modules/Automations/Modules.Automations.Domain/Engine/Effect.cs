using Modules.Automations.Domain.Flow;

namespace Modules.Automations.Domain.Engine;

/// <summary>
/// Lo que el motor le pide al mundo (spec 2026-10-21 D-M2). El procesador los ejecuta en orden; <see cref="CallHttp"/> y
/// <see cref="CheckBusinessHours"/> vuelven al motor como <see cref="EngineInput.HttpResult"/> y <see cref="EngineInput.BusinessHours"/>.
/// </summary>
public abstract record Effect(string NodeId)
{
    public sealed record SendText(string NodeId, string Text) : Effect(NodeId);

    public sealed record SendInteractive(string NodeId, InteractiveMessage Message) : Effect(NodeId);

    /// <summary>Spec 2026-11-18: el archivo de un Mensaje. <see cref="Kind"/> es uno de <see cref="ChatbotMediaKinds"/> y <see cref="FileId"/> el
    /// id de Storage, que quien ejecuta resuelve por tenant. <see cref="Caption"/> y <see cref="UrlButton"/> ya vienen interpolados (un pie en
    /// blanco es <c>null</c>); un audio nunca los lleva: su texto o su botón salen después, en otro efecto.</summary>
    public sealed record SendMedia(string NodeId, string Kind, Guid FileId, string? Caption, UrlButton? UrlButton) : Effect(NodeId);

    public sealed record CallHttp(string NodeId, HttpNode Node) : Effect(NodeId);

    public sealed record CheckBusinessHours(string NodeId) : Effect(NodeId);

    /// <summary>Spec 2026-11-19 (chatbot CRM action): the «Acción de CRM» block asks for its operation, as the system; it comes back as
    /// <see cref="EngineInput.CrmResult"/>. The simulator answers it without touching the CRM.</summary>
    public sealed record CrmAction(string NodeId, CrmActionNode Node) : Effect(NodeId);

    /// <summary><see cref="StepTitle"/> es el título del bloque: lo que la bandeja muestra como paso del asistente.</summary>
    public sealed record WaitForCustomer(string NodeId, string StepTitle) : Effect(NodeId);

    /// <summary>Spec 2026-10-24 D-T9: <see cref="Reason"/> es uno de <see cref="HandoffReasons"/>; lo dice el motor, que es quien sabe por
    /// qué camino llegó, en vez de que el procesador lo deduzca por el tipo de bloque. <see cref="MemberId"/> (spec 2026-10-25 D-1) es a quién
    /// se asigna; <c>null</c> deja la conversación sin asignar.
    /// <see cref="Code"/> (spec 2026-11-17 adenda D-6) sólo con <see cref="HandoffReasons.AgentBlockOutputUnhandled"/>: la salida del bloque
    /// «Agente IA» que no estaba conectada.</summary>
    public sealed record Handoff(string NodeId, string Reason = HandoffReasons.Block, Guid? MemberId = null, string? Code = null) : Effect(NodeId);

    /// <summary>Spec 2026-11-17 (agente IA en chatbots) §6.1 y D-B4: el flujo le pasa la conversación al agente. <see cref="Context"/> ya viene
    /// interpolado y cortado; el resto es el bloque tal cual. No vuelve en la misma unidad: el agente devuelve con
    /// <see cref="EngineInput.AgentResult"/> minutos u horas después.</summary>
    public sealed record DelegateToAgent(string NodeId, Guid AgentId, string Objective, string Context, string FirstTurn, int IdleMinutes)
        : Effect(NodeId);

    public sealed record Resolve(string NodeId, Guid? ReasonId) : Effect(NodeId);

    /// <summary>
    /// El <c>End</c> de D-M2: la corrida terminó sin un bloque terminal; <see cref="Reason"/> es uno de <see cref="EndReasons"/>. Se
    /// llama <c>EndRun</c> porque <c>End</c> es palabra reservada de VB (CA1716, que el build trata como error).
    /// </summary>
    public sealed record EndRun(string NodeId, string Reason) : Effect(NodeId);

    /// <summary>Spec 2026-10-26 D-2: el bloque Política de datos pregunta si el cliente ya aceptó; vuelve como
    /// <see cref="EngineInput.DataConsent"/>.</summary>
    public sealed record CheckDataConsent(string NodeId) : Effect(NodeId);

    /// <summary>Spec 2026-10-26 D-6: la respuesta del cliente, con el texto que se le mostró (<see cref="ShownText"/>) y el mensaje en que
    /// se le preguntó, si se conoce.</summary>
    public sealed record RecordDataConsent(string NodeId, DataConsentDecision Decision, string ShownText, Guid? PromptMessageId)
        : Effect(NodeId);

    /// <summary>Spec 2026-10-26 D-4: se agotaron los intentos sin una respuesta válida. No se registra nada; el bloque sale por
    /// <c>rejected</c> sólo en <c>rejectedOutput</c>; en <c>repeat</c> y <c>handoff</c> sigue un <c>Handoff</c> (spec 2026-10-26 repetir R-4).</summary>
    public sealed record DataConsentUnanswered(string NodeId) : Effect(NodeId);
}

/// <summary>Spec 2026-10-26 D-6: qué respondió el cliente a la política de datos.</summary>
public enum DataConsentDecision
{
    Accepted,
    Rejected,
}

/// <summary>Spec 2026-10-24 D-T9: por qué el motor pidió un <see cref="Effect.Handoff"/>.</summary>
public static class HandoffReasons
{
    /// <summary>El flujo llegó a un bloque «Pasar a un asesor».</summary>
    public const string Block = "block";

    /// <summary>Botones o Lista con <c>fallback = handoff</c> y una respuesta inválida.</summary>
    public const string InvalidReply = "invalid_reply";

    /// <summary>Spec 2026-10-25 D-2: se agotaron los intentos y la regla del chatbot es pasar a un asesor.</summary>
    public const string AttemptsExhausted = "attempts_exhausted";

    /// <summary>Spec 2026-10-26 repetir R-3 y R-4c: Política de datos con «No acepto» al tope de <c>repeat</c>, o al primero en
    /// <c>handoff</c>.</summary>
    public const string DataConsentRejected = "data_consent_rejected";

    /// <summary>Spec 2026-10-26 repetir R-4b: Política de datos en <c>repeat</c> o <c>handoff</c> sin cliente al que registrarle la
    /// respuesta.</summary>
    public const string DataConsentUnavailable = "data_consent_unavailable";

    /// <summary>Spec 2026-11-17 D-B12 y D-B13: el bloque «Agente IA» volvió por <c>human</c>, <c>noAnswer</c> o <c>error</c> y esa salida no
    /// está conectada; la conversación no puede quedar sin nadie.</summary>
    public const string AgentBlockOutputUnhandled = "agent_block_output_unhandled";
}

/// <summary>Spec 2026-11-17 §6.3: los <c>reason</c> del bloque «Agente IA» que decide Automations (el resto los escribe Ai al devolver). Cumplen
/// la regla de códigos del hilo (<c>^[a-z0-9_.]{1,64}$</c>).</summary>
public static class AgentBlockReasons
{
    /// <summary>El bloque no tiene agente: sale por <c>error</c> sin delegar.</summary>
    public const string AgentMissing = "agent_missing";

    /// <summary>D-B16: el tenant apagó <c>ai</c>; el bloque sale por <c>error</c> sin delegar.</summary>
    public const string ModuleDisabled = "module_disabled";

    public const string WindowClosed = "window_closed";

    public const string Blocked = "blocked";
}

public static class EndReasons
{
    /// <summary>§5.4: una salida sin conectar (sólo posible si se publicó con avisos) termina la corrida <c>Completed</c>.</summary>
    public const string OutputUnconnected = "output_unconnected";

    /// <summary>Ruling P2-R13: el flujo dio vueltas sin esperar al cliente ni a un efecto externo; la corrida termina <c>Failed</c>.</summary>
    public const string LoopDetected = "loop_detected";

    /// <summary>Ruling P2-R14: una salida (o el Inicio) apunta a un bloque que no existe en la versión; la corrida termina <c>Failed</c>.</summary>
    public const string TargetMissing = "target_missing";

    /// <summary>Spec 2026-11-17 §6.4: el agente volvió sin salida porque la ventana del canal se cerró; la corrida termina <c>WindowClosed</c>.</summary>
    public const string WindowClosed = "window_closed";

    /// <summary>Spec 2026-11-17 §6.4: el agente volvió sin salida porque el contacto está bloqueado; la corrida termina <c>Interrupted</c>.</summary>
    public const string Interrupted = "interrupted";
}

public sealed record StepResult(RunState State, IReadOnlyList<Effect> Effects)
{
    /// <summary>Spec 2026-10-25 D-E5: un registro por bloque visitado en este paso. Es propiedad y no parámetro para que la igualdad de
    /// los resultados que comparan las pruebas no cambie.</summary>
    public IReadOnlyList<StepRecord> Steps { get; init; } = [];
}
