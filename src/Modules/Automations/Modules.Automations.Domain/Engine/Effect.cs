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

public sealed record StepResult(RunState State, IReadOnlyList<Effect> Effects);
