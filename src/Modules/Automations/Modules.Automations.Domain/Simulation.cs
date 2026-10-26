using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Modules.Automations.Domain.Engine;

namespace Modules.Automations.Domain;

/// <summary>
/// Spec 2026-10-21 §8 y D-S1: una sesión del simulador del editor sobre el borrador. Guarda el estado del motor y la transcripción en
/// <c>automations.simulations</c> (jsonb) para sobrevivir réplicas, y vence a las <see cref="Lifetime"/> del último mensaje. No deja rastro en
/// Mensajería: la conversación es de mentira. La ve cualquiera del tenant con <c>automations.chatbot.manage</c>; <see cref="MemberId"/> es
/// sólo quién la abrió.
/// </summary>
public sealed class Simulation
{
    /// <summary>D-S1: el TTL de 2 h, que se renueva con cada mensaje.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(2);

    /// <summary>Las líneas que guarda la transcripción, las más nuevas: probar un flujo dos horas seguidas no hace crecer la fila sin fin
    /// (cada mensaje la reescribe entera).</summary>
    public const int TranscriptMax = 500;

    // El estado por nombre (Waiting, HandedOff), como conversation_runs.status: la fila se puede leer en la base.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private Simulation()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ChatbotId { get; private set; }

    public Guid MemberId { get; private set; }

    /// <summary>D-S2: «llamar a la API de verdad». Apagado, el bloque HTTP usa su respuesta de ejemplo.</summary>
    public bool LiveHttp { get; private set; }

    /// <summary>El <see cref="RunState"/> en JSON (<see cref="ToState"/>).</summary>
    public string StateJson { get; private set; } = "{}";

    /// <summary>Las <see cref="TranscriptEntry"/> en JSON, de la más vieja a la más nueva (<see cref="ReadTranscript"/>).</summary>
    public string TranscriptJson { get; private set; } = "[]";

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public static Simulation Start(Guid tenantId, Guid chatbotId, Guid memberId, bool liveHttp, RunState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        var simulation = new Simulation
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ChatbotId = chatbotId,
            MemberId = memberId,
            LiveHttp = liveHttp,
            CreatedAt = now,
        };
        simulation.Apply(state, [], now);
        return simulation;
    }

    /// <summary>El paso que dio el motor: el estado nuevo y las líneas que agregó. Renueva el vencimiento (D-S1).</summary>
    public void Apply(RunState state, IReadOnlyList<TranscriptEntry> appended, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(appended);
        StateJson = WriteState(state);
        var transcript = ReadTranscript().Concat(appended).ToList();
        TranscriptJson = JsonSerializer.Serialize(transcript.Skip(Math.Max(0, transcript.Count - TranscriptMax)), JsonOptions);
        UpdatedAt = now;
        ExpiresAt = now + Lifetime;
    }

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    /// <summary>Terminó (pasó a un asesor, resolvió, salió por una salida sin conectar o falló): el motor ya no espera nada.</summary>
    public bool IsEnded => ToState().Status is not (RunStatus.Running or RunStatus.Waiting);

    // Un método y no una propiedad: EF tomaría una colección de sólo lectura por una navegación.
    public IReadOnlyList<TranscriptEntry> ReadTranscript() =>
        JsonSerializer.Deserialize<List<TranscriptEntry>>(TranscriptJson, JsonOptions) ?? [];

    public RunState ToState()
    {
        var row = JsonSerializer.Deserialize<StateRow>(StateJson, JsonOptions)
            ?? throw new JsonException("El estado de la simulación está vacío.");
        return new RunState(
            row.Status, row.CurrentNodeId, row.StepNumber, row.Attempts, RunVariables.Parse(row.Variables?.ToJsonString() ?? string.Empty),
            [.. (row.Trace ?? []).Select(entry => new TraceEntry(entry.At, entry.NodeId, entry.Kind, entry.Outcome))],
            row.ConsentPrompt);
    }

    // Las variables van como objeto JSON y no como texto escapado, igual que conversation_runs.variables: se pueden leer en la base.
    private static string WriteState(RunState state) => JsonSerializer.Serialize(
        new StateRow(
            state.Status, state.CurrentNodeId, state.StepNumber, state.Attempts, JsonNode.Parse(state.Variables.ToJson()) as JsonObject,
            [.. state.Trace.Select(entry => new TraceRow(entry.At, entry.NodeId, entry.Kind, entry.Outcome))], state.ConsentPrompt),
        JsonOptions);

    // ConsentPrompt (D-6) llega al final y nulo: un state guardado antes de esta clave se lee sin ella.
    private sealed record StateRow(
        RunStatus Status, string? CurrentNodeId, int StepNumber, int Attempts, JsonObject? Variables, IReadOnlyList<TraceRow>? Trace,
        ConsentPrompt? ConsentPrompt = null);

    private sealed record TraceRow(DateTimeOffset At, string NodeId, string Kind, string Outcome);
}

/// <summary>
/// Spec 2026-10-21 §8: una línea de la transcripción del simulador. <see cref="From"/> es uno de <see cref="TranscriptSenders"/>. Una línea
/// del bot trae lo que el mensaje ofrecía (<see cref="Buttons"/>, <see cref="List"/> o <see cref="UrlButton"/>) para que el panel lo pinte
/// como lo vería el cliente y el tester toque una opción; <see cref="NodeId"/> y <see cref="NodeTitle"/> dicen qué bloque la escribió
/// (<c>null</c> en las del tester).
/// </summary>
public sealed record TranscriptEntry(
    string From,
    string? NodeId,
    string? NodeTitle,
    string Text,
    IReadOnlyList<TranscriptButton>? Buttons,
    TranscriptList? List,
    TranscriptUrlButton? UrlButton,
    DateTimeOffset At);

/// <summary>Un botón de respuesta: <see cref="Id"/> es el <c>replyId</c> que el panel manda al tocarlo.</summary>
public sealed record TranscriptButton(string Id, string Title);

/// <summary>Una lista con sus filas ya ajustadas a los topes de Meta (ruling P2-R15), como las recibiría el cliente.</summary>
public sealed record TranscriptList(string ButtonLabel, IReadOnlyList<TranscriptRow> Rows);

/// <summary>Una fila: <see cref="Id"/> es el <c>replyId</c> que el panel manda al elegirla.</summary>
public sealed record TranscriptRow(string Id, string Title, string? Description);

/// <summary>El botón que abre una URL de un Mensaje con <c>urlButton</c> (D-I1). No responde nada: no lleva <c>replyId</c>.</summary>
public sealed record TranscriptUrlButton(string Label, string Url);

public static class TranscriptSenders
{
    public const string Bot = "bot";
    public const string Customer = "customer";

    /// <summary>Lo que el simulador explica y el cliente no vería: un traspaso, una resolución, un final o un bloque HTTP sin ejemplo.</summary>
    public const string System = "system";
}
