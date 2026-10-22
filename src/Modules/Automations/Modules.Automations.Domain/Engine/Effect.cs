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

    public sealed record Handoff(string NodeId) : Effect(NodeId);

    public sealed record Resolve(string NodeId, Guid? ReasonId) : Effect(NodeId);

    /// <summary>
    /// El <c>End</c> de D-M2: la corrida terminó sin un bloque terminal; <see cref="Reason"/> es uno de <see cref="EndReasons"/>. Se
    /// llama <c>EndRun</c> porque <c>End</c> es palabra reservada de VB (CA1716, que el build trata como error).
    /// </summary>
    public sealed record EndRun(string NodeId, string Reason) : Effect(NodeId);
}

public static class EndReasons
{
    /// <summary>§5.4: una salida sin conectar (sólo posible si se publicó con avisos) termina la corrida <c>Completed</c>.</summary>
    public const string OutputUnconnected = "output_unconnected";
}

public sealed record StepResult(RunState State, IReadOnlyList<Effect> Effects);
