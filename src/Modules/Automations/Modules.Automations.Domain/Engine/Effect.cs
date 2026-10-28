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

    public sealed record CallHttp(string NodeId, HttpNode Node) : Effect(NodeId);

    public sealed record CheckBusinessHours(string NodeId) : Effect(NodeId);

    /// <summary><see cref="StepTitle"/> es el título del bloque: lo que la bandeja muestra como paso del asistente.</summary>
    public sealed record WaitForCustomer(string NodeId, string StepTitle) : Effect(NodeId);

    /// <summary>Spec 2026-10-24 D-T9: <see cref="Reason"/> es uno de <see cref="HandoffReasons"/>; lo dice el motor, que es quien sabe por
    /// qué camino llegó, en vez de que el procesador lo deduzca por el tipo de bloque. <see cref="MemberId"/> (spec 2026-10-25 D-1) es a quién
    /// se asigna; <c>null</c> deja la conversación sin asignar.</summary>
    public sealed record Handoff(string NodeId, string Reason = HandoffReasons.Block, Guid? MemberId = null) : Effect(NodeId);

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
}

public static class EndReasons
{
    /// <summary>§5.4: una salida sin conectar (sólo posible si se publicó con avisos) termina la corrida <c>Completed</c>.</summary>
    public const string OutputUnconnected = "output_unconnected";

    /// <summary>Ruling P2-R13: el flujo dio vueltas sin esperar al cliente ni a un efecto externo; la corrida termina <c>Failed</c>.</summary>
    public const string LoopDetected = "loop_detected";

    /// <summary>Ruling P2-R14: una salida (o el Inicio) apunta a un bloque que no existe en la versión; la corrida termina <c>Failed</c>.</summary>
    public const string TargetMissing = "target_missing";
}

public sealed record StepResult(RunState State, IReadOnlyList<Effect> Effects)
{
    /// <summary>Spec 2026-10-25 D-E5: un registro por bloque visitado en este paso. Es propiedad y no parámetro para que la igualdad de
    /// los resultados que comparan las pruebas no cambie.</summary>
    public IReadOnlyList<StepRecord> Steps { get; init; } = [];
}
