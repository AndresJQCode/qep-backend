using System.Text.Json;
using Modules.Automations.Domain.Engine;

namespace Modules.Automations.Domain;

/// <summary>
/// Spec 2026-10-21 §4.2 y D-M12: una corrida de un chatbot sobre una conversación (a lo sumo una viva por conversación, índice
/// <c>IX_conversation_runs_live</c>). El motor es puro: esta entidad sólo guarda el <see cref="RunState"/> entre dos pasos y lo devuelve.
/// <c>ConversationId</c> y <c>ConnectionId</c> son referencias blandas a Messaging e Integrations.
/// </summary>
public sealed class ConversationRun
{
    private static readonly JsonSerializerOptions TraceOptions = new(JsonSerializerDefaults.Web);

    private ConversationRun()
    {
        VariablesJson = "{}";
        TraceJson = "[]";
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ConversationId { get; private set; }

    public Guid ConnectionId { get; private set; }

    public Guid ChatbotId { get; private set; }

    /// <summary>D-C1: la corrida termina con la versión con que empezó.</summary>
    public Guid ChatbotVersionId { get; private set; }

    public RunStatus Status { get; private set; }

    public string? CurrentNodeId { get; private set; }

    public int StepNumber { get; private set; }

    public int Attempts { get; private set; }

    public string VariablesJson { get; private set; }

    /// <summary><c>[{at, nodeId, kind, outcome}]</c>, tope <see cref="RunState.TraceMax"/> entradas.</summary>
    public string TraceJson { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    public string? FailureCode { get; private set; }

    /// <summary>Ruling P2-R24: el último entrante del inbox que procesó la corrida, escrito en la misma transacción que su estado. Si la fila
    /// se vuelve a reclamar después de confirmada la unidad (el cierre de la fila falló o el lease venció), el procesador la reconoce y no la
    /// toma por una respuesta nueva.</summary>
    public Guid? LastInboundMessageId { get; private set; }

    public bool IsLive => Status is RunStatus.Running or RunStatus.Waiting;

    /// <summary>Ruling P2-R5: el <paramref name="id"/> lo deriva el procesador del messageId del inbox, así un Start deshecho y
    /// reintentado reusa el mismo id (y los mismos clientIds).</summary>
    public static ConversationRun Start(
        Guid id, Guid tenantId, Guid conversationId, Guid connectionId, Guid chatbotId, Guid chatbotVersionId, RunState initial,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(initial);
        var run = new ConversationRun
        {
            Id = id,
            TenantId = tenantId,
            ConversationId = conversationId,
            ConnectionId = connectionId,
            ChatbotId = chatbotId,
            ChatbotVersionId = chatbotVersionId,
            StartedAt = now,
        };
        run.Apply(initial, now);
        return run;
    }

    /// <summary>Copia el estado que devolvió el motor; si es terminal, la corrida termina ahora.</summary>
    public void Apply(RunState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        Status = state.Status;
        CurrentNodeId = state.CurrentNodeId;
        StepNumber = state.StepNumber;
        Attempts = state.Attempts;
        VariablesJson = state.Variables.ToJson();
        TraceJson = JsonSerializer.Serialize(state.Trace.Select(entry => new TraceRow(entry.At, entry.NodeId, entry.Kind, entry.Outcome)), TraceOptions);
        UpdatedAt = now;
        EndedAt = IsLive ? null : now;
    }

    /// <summary>Termina la corrida por fuera del motor: <c>Interrupted</c>, <c>WindowClosed</c>, <c>Failed</c>, <c>Abandoned</c> o
    /// <c>Completed</c> (la palabra de reinicio con una versión nueva publicada, 2026-10-23).</summary>
    public void Finish(RunStatus status, string? failureCode, DateTimeOffset now)
    {
        if (!IsLive)
        {
            throw new InvalidOperationException($"La corrida {Id} ya terminó como {Status}.");
        }

        if (status is RunStatus.Running or RunStatus.Waiting)
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Finish cierra la corrida; no puede dejarla viva.");
        }

        Status = status;
        FailureCode = failureCode;
        UpdatedAt = now;
        EndedAt = now;
    }

    /// <summary>
    /// Copia el estado que devolvió el motor (variables, trace, bloque) pero termina con el desenlace que decidió el procesador: el
    /// envío que Mensajería no aceptó (<c>Failed</c> con su código, <c>WindowClosed</c>), la conversación que dejó de ser del bot a mitad
    /// de la unidad (<c>Interrupted</c>) o el <c>EndRun</c> de un ciclo o de un bloque que no existe (<c>Failed</c>, P2-R13/R14). Así lo
    /// capturado antes del corte queda en «Datos capturados» (§5.5). Como <see cref="Finish"/>, sólo sobre una corrida viva.
    /// </summary>
    public void Close(RunState state, RunStatus status, string? failureCode, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!IsLive)
        {
            throw new InvalidOperationException($"La corrida {Id} ya terminó como {Status}.");
        }

        if (status is RunStatus.Running or RunStatus.Waiting)
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Close cierra la corrida; no puede dejarla viva.");
        }

        Apply(state with { Status = status }, now);
        FailureCode = failureCode;
    }

    /// <summary>P2-R24: también sobre una corrida ya terminada en esta unidad (resuelta, interrumpida): el reintento la encuentra por
    /// <c>FindLatestAsync</c>.</summary>
    public void MarkInbound(Guid messageId) => LastInboundMessageId = messageId;

    public RunState ToState()
    {
        var rows = JsonSerializer.Deserialize<List<TraceRow>>(TraceJson, TraceOptions) ?? [];
        return new RunState(
            Status, CurrentNodeId, StepNumber, Attempts, RunVariables.Parse(VariablesJson),
            rows.Select(row => new TraceEntry(row.At, row.NodeId, row.Kind, row.Outcome)).ToList());
    }

    private sealed record TraceRow(DateTimeOffset At, string NodeId, string Kind, string Outcome);
}
