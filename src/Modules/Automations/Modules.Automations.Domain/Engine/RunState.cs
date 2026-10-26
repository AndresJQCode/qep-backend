namespace Modules.Automations.Domain.Engine;

/// <summary>Spec 2026-10-21 §4.2 (<c>conversation_runs.status</c>): <see cref="Running"/> y <see cref="Waiting"/> son las corridas vivas
/// (D-M12); el resto termina la corrida.</summary>
public enum RunStatus
{
    Running,
    Waiting,
    Completed,
    HandedOff,
    Resolved,
    WindowClosed,
    Failed,
    Abandoned,
    Interrupted,
}

/// <summary>Una entrada de <c>conversation_runs.trace</c> (<c>{at, nodeId, kind, outcome}</c>): por qué salida dejó la corrida el bloque, o
/// qué quedó esperando en él (<see cref="TraceOutcomes"/>).</summary>
public sealed record TraceEntry(DateTimeOffset At, string NodeId, string Kind, string Outcome);

/// <summary>Lo que el trace anota de un bloque que no sale por una de sus salidas de §5.4 al entrar.</summary>
public static class TraceOutcomes
{
    /// <summary>Pregunta abierta, Botones, Lista o Pedir adjunto: envió su mensaje y espera al cliente.</summary>
    public const string Waiting = "waiting";

    /// <summary>Llamada HTTP u Horario de atención: pidió al procesador el efecto externo y espera su resultado.</summary>
    public const string Pending = "pending";

    public const string HandedOff = "handedOff";
    public const string Resolved = "resolved";

    /// <summary>La palabra de reinicio (D-I5) o los intentos agotados sin salida conectada (D-M7): la corrida vuelve al Inicio desde el
    /// bloque que esperaba.</summary>
    public const string Restart = "restart";
}

/// <summary>Spec 2026-10-26 D-6: lo que se le mostró al cliente al pedirle la política de datos. El registro guarda este texto, no la
/// plantilla interpolada de nuevo al responder: una variable puede cambiar entre la pregunta y la respuesta. <see cref="PromptMessageId"/>
/// es el mensaje enviado, si el procesador lo conoce.</summary>
public sealed record ConsentPrompt(string NodeId, string ShownText, Guid? PromptMessageId);

/// <summary>
/// El estado de una corrida entre dos pasos del motor (spec 2026-10-21 D-M2): lo que <see cref="FlowEngine.Step"/> recibe y devuelve y lo
/// que el procesador persiste en <c>conversation_runs</c>. <see cref="Attempts"/> son las respuestas inválidas al bloque que espera
/// (D-M7). El motor nunca modifica las <see cref="Variables"/> que recibe: trabaja sobre una copia.
/// </summary>
/// <param name="Status">Viva (<c>Running</c>, <c>Waiting</c>) o cómo terminó.</param>
/// <param name="CurrentNodeId">El último bloque en que entró la corrida; <c>null</c> antes de empezar.</param>
/// <param name="StepNumber">Ruling P2-R5: sube con cada bloque en que se entra (también al reenviar el de espera), así el procesador
/// deriva clientIds únicos de (runId, StepNumber al empezar la unidad, índice del envío) y un reintento de la fila no duplica mensajes.</param>
/// <param name="Attempts">Respuestas inválidas al bloque que espera; vuelve a cero al entrar en otro bloque.</param>
/// <param name="Variables">Las variables de la corrida (D-M8).</param>
/// <param name="Trace">Las últimas <see cref="TraceMax"/> entradas, de la más vieja a la más nueva.</param>
/// <param name="ConsentPrompt">Spec 2026-10-26 D-6: la pregunta de Política de datos que espera respuesta; <c>null</c> en cualquier otro
/// momento.</param>
public sealed record RunState(
    RunStatus Status, string? CurrentNodeId, int StepNumber, int Attempts, RunVariables Variables, IReadOnlyList<TraceEntry> Trace,
    ConsentPrompt? ConsentPrompt = null)
{
    /// <summary>§4.2: el trace guarda sólo las últimas 200 entradas.</summary>
    public const int TraceMax = 200;

    /// <summary>Una corrida nueva: sólo las variables fijas (<c>nombre</c>, <c>telefono</c>, <c>empresa</c>, <c>conexion</c>).</summary>
    public static RunState Initial(RunVariables fixedVariables)
    {
        ArgumentNullException.ThrowIfNull(fixedVariables);
        return new RunState(RunStatus.Running, null, 0, 0, fixedVariables, []);
    }
}
