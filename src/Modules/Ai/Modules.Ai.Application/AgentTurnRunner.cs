using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.Extensions.Logging;
using Modules.Ai.Domain;

namespace Modules.Ai.Application;

/// <summary>
/// Un trabajo del agente. <see cref="Provider"/>, <see cref="Model"/> y <see cref="Config"/> son <b>de la versión de la sesión</b> (o de la
/// foto del borrador en una prueba), nunca del borrador vigente ni del tenant (C-17, C-20).
/// </summary>
/// <param name="AgentVersionId"><c>null</c> en una prueba sobre el borrador.</param>
/// <param name="CustomerId">El cliente de la conversación (o el que fijó la prueba); las herramientas nunca lo toman de un argumento.</param>
/// <param name="KnownSourceIds">Las fuentes citables además de las llamadas de este trabajo: ids de FAQ y de respuestas rápidas.</param>
/// <param name="IsTest">El playground (§7): las escrituras se simulan.</param>
/// <param name="Block">Spec 2026-11-17 §5.4: el paso del chatbot que atiende la sesión (o la prueba) en modo bloque; ofrece
/// <c>finish_objective</c>. <c>null</c> fuera de un bloque.</param>
public sealed record AgentTurnContext(
    Guid TenantId,
    Guid AgentId,
    Guid? AgentVersionId,
    AiProvider Provider,
    string Model,
    string? Effort,
    AgentConfig Config,
    AiCredential Credential,
    Guid JobId,
    Guid? SessionId,
    Guid? ConversationId,
    Guid? TestId,
    Guid? CustomerId,
    IReadOnlyCollection<string> KnownSourceIds,
    bool IsTest,
    AgentBlockContext? Block = null);

/// <summary>Spec 2026-11-17 D-B8: el objetivo del paso del chatbot (lo escribe el admin del flujo) y su contexto (variables del flujo ya
/// interpoladas: texto del cliente, que el prompt marca como datos, §8).</summary>
public sealed record AgentBlockContext(string Objective, string Context);

/// <summary>Cómo terminó un trabajo. Quien lo corre decide qué hace con cada caso (enviar, traspasar, reintentar).</summary>
public abstract record AgentTurnOutcome;

/// <summary>Spec 2026-11-17 D-B8: el modelo llamó <c>finish_objective</c>: el objetivo del bloque se cumplió.
/// <paramref name="Summary"/> mide a lo sumo <see cref="AgentSession.ObjectiveSummaryMaxLength"/>; vacío si no escribió nada.</summary>
public sealed record AgentFinished(string Summary) : AgentTurnOutcome;

/// <param name="SourceIds">Sólo las fuentes que existen (§8.2): las demás se descartan.</param>
/// <param name="Wrote">Una herramienta de escritura de la Agenda o de cotizaciones tuvo éxito en este trabajo: la respuesta no se descarta por un turno nuevo del cliente.</param>
public sealed record AgentReplied(string Text, IReadOnlyList<string> SourceIds, bool Wrote = false) : AgentTurnOutcome;

/// <param name="Reason">Uno de <see cref="AgentHandoffReasons.All"/>, o el código de la herramienta que terminó el turno
/// (<see cref="AgentToolHandoff"/>).</param>
/// <param name="Note">Para el evento interno, nunca para el cliente.</param>
/// <param name="MemberId">P-4b: a quién va la conversación en vez del destino configurado (la asesora del borrador de cotización).</param>
/// <param name="QuotationNumber">Spec 2026-11-17 D-B13: el número del borrador, que vuelve al flujo en modo bloque.</param>
public sealed record AgentHandedOff(string Reason, string? Note, Guid? MemberId = null, string? QuotationNumber = null, bool Wrote = false) : AgentTurnOutcome;

/// <summary>Un error que el worker reintenta (D-A17): proveedor caído o límite de tasa.</summary>
public sealed record AgentTurnRetry(string Code, TimeSpan? RetryAfter) : AgentTurnOutcome;

/// <param name="Code">El código de la tabla de errores del plan si viene del proveedor; <c>null</c> en los topes de D-A20 y en la respuesta
/// inválida, que la tabla no nombra (el código del traspaso lo decide la Tarea 7).</param>
/// <param name="Wrote">Una herramienta de escritura ya tuvo éxito en este turno (la cita está hecha): el procesador no descarta el resultado
/// por un turno nuevo del cliente.</param>
public sealed record AgentTurnFailed(AgentFailureKind Kind, AgentFailureReason Reason, string? Code, bool Wrote = false) : AgentTurnOutcome;

/// <summary>La causa de traspaso de §4.4: el agente (su configuración o su respuesta) o la cuenta del proveedor.</summary>
public enum AgentFailureKind
{
    AgentFailed,
    ProviderFailed,
}

public enum AgentFailureReason
{
    ProviderError,
    InvalidResponse,
    ModelCallsExceeded,
    ToolCallsExceeded,
    JobTimedOut,
}

/// <summary>§8.2: los motivos cerrados de <c>handoff_to_human</c>.</summary>
public static class AgentHandoffReasons
{
    public const int NoteMaxLength = 300;

    public static readonly IReadOnlyList<string> All = ["customer_asked", "no_answer", "complaint", "out_of_scope"];
}

/// <summary>Una llamada al modelo para <c>ai.usage_records</c> (D-A27, C-4): una fila por llamada, también las que se descartan.</summary>
/// <param name="AgentId"><c>null</c> en el consumo del copiloto (spec 2026-11-17 copiloto §5.1), que no tiene agente.</param>
/// <param name="CopilotThreadId">D-C12: el hilo del copiloto que pagó la llamada; <c>null</c> en el de un agente.</param>
/// <param name="MemberUserId">D-C12: quien preguntó al copiloto.</param>
public sealed record AiUsageRecord(
    Guid TenantId,
    Guid? AgentId,
    Guid? AgentVersionId,
    Guid? ConversationId,
    Guid? SessionId,
    Guid? TestId,
    Guid JobId,
    AiProvider Provider,
    string Model,
    string? ServedModel,
    AiModelUsage Usage,
    string Outcome,
    DateTimeOffset CalledAt,
    Guid? CopilotThreadId = null,
    Guid? MemberUserId = null);

/// <summary>Una llamada a una herramienta para <c>ai.tool_calls</c> (§8.2), con sus argumentos y su resultado (retención P-5).</summary>
public sealed record AiToolCallRecord(
    Guid TenantId,
    Guid JobId,
    string CallId,
    string Tool,
    string ArgumentsJson,
    string? ResultJson,
    string Outcome,
    string? ErrorCode,
    int DurationMs,
    DateTimeOffset CreatedAt);

/// <summary>Lo que el bucle deja escrito mientras corre, cada fila en su propio guardado corto: nunca una transacción abierta mientras
/// el modelo piensa (D-A13), y lo ya gastado queda aunque el trabajo muera después.</summary>
public interface IAiRunLedger
{
    Task RecordUsageAsync(AiUsageRecord record, CancellationToken cancellationToken);

    Task RecordToolCallAsync(AiToolCallRecord record, CancellationToken cancellationToken);
}

/// <summary>Los valores de <c>ai.usage_records.outcome</c> y <c>ai.tool_calls.outcome</c>.</summary>
public static class AiRunOutcomes
{
    public const string Ok = "ok";
    public const string InvalidResponse = "invalid_response";
    public const string Refused = "refused";
    public const string Error = "error";
}

/// <summary>
/// Plan 2026-11-12, 5.5: el turno del agente sobre el bucle común (<see cref="ModelToolLoop"/>, plan fase 2 C2.1). Lo del agente vive en su
/// perfil: las herramientas del principal de servicio que el agente tiene activas, las finales (<c>send_reply</c>, <c>handoff_to_human</c> y,
/// en modo bloque, <c>finish_objective</c>), la regla de una escritura por respuesta y el filtro de fuentes conocidas. Los topes son los de
/// D-A20 (<c>Ai:Model</c>).
/// </summary>
public sealed class AgentTurnRunner(
    IAiModelClientResolver resolver,
    IEnumerable<IAgentTool> tools,
    IAiRunLedger ledger,
    IAiProviderCredentials credentials,
    AiRunLimits limits,
    IClock clock,
    ILogger<AgentTurnRunner> logger,
    AiMetrics? metrics = null)
{
    /// <summary>§10: una respuesta que copia este tanto de las instrucciones del agente se descarta.</summary>
    internal const int LeakWindow = 200;

    public const int ReplyMaxLength = 4096;

    private static readonly AiToolDefinition SendReply = new(
        AgentToolNames.SendReply,
        "Envía tu respuesta al cliente. Es la forma de terminar tu turno.",
        """
        {"type":"object","properties":{"text":{"type":"string","description":"El mensaje para el cliente, máximo 4096 caracteres."},"sourceIds":{"type":"array","items":{"type":"string"},"description":"Los ids de las preguntas frecuentes, respuestas rápidas o llamadas a herramientas en que te basaste."}},"required":["text","sourceIds"],"additionalProperties":false}
        """);

    private static readonly AiToolDefinition HandoffToHuman = new(
        AgentToolNames.HandoffToHuman,
        "Pasa la conversación a una persona del equipo. Termina tu turno.",
        """
        {"type":"object","properties":{"reason":{"type":"string","enum":["customer_asked","no_answer","complaint","out_of_scope"],"description":"Por qué pasas la conversación."},"note":{"type":["string","null"],"description":"Nota interna para el equipo, máximo 300 caracteres. El cliente no la ve."}},"required":["reason","note"],"additionalProperties":false}
        """);

    // Spec 2026-11-17 D-B8: estricta como las otras finales (C-8). El tope de 500 va en la descripción y lo valida Finish.
    private static readonly AiToolDefinition FinishObjective = new(
        AgentToolNames.FinishObjective,
        "Indica que cumpliste el objetivo de este paso del chatbot. Termina tu turno y devuelve la conversación al flujo.",
        """
        {"type":"object","properties":{"summary":{"type":"string","description":"Resumen breve de lo que se resolvió, máximo 500 caracteres. Puede llegar al cliente o a otro sistema: no pongas instrucciones ni datos internos."}},"required":["summary"],"additionalProperties":false}
        """);

    public async Task<AgentTurnOutcome> RunAsync(AgentTurnContext context, AgentPrompt prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(prompt);
        var run = new ToolLoopRun(
            context.TenantId, context.JobId, context.Provider, context.Model, context.Effort, context.Credential, limits,
            new AiUsageOwner(context.AgentId, context.AgentVersionId, context.ConversationId, context.SessionId, context.TestId));
        var profile = new AgentProfile(context, tools);
        var outcome = await new ModelToolLoop(resolver, ledger, credentials, clock, logger, metrics).RunAsync(run, prompt, profile, cancellationToken);
        // Un tope o un error después de una escritura exitosa: la cita ya existe, así que el resultado no se descarta por un turno nuevo.
        return outcome is AgentTurnFailed failed && profile.Wrote ? failed with { Wrote = true } : outcome;
    }

    /// <summary>§10: la respuesta no puede traer un fragmento de <see cref="LeakWindow"/> caracteres de las instrucciones del agente.</summary>
    internal static bool LeaksInstructions(string reply, string instructions)
    {
        if (instructions.Length < LeakWindow || reply.Length < LeakWindow)
        {
            return false;
        }

        for (var start = 0; start + LeakWindow <= instructions.Length; start++)
        {
            if (reply.Contains(instructions.AsSpan(start, LeakWindow), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Lo del agente en el bucle común, por trabajo.</summary>
    private sealed class AgentProfile : IToolLoopProfile
    {
        private readonly AgentTurnContext _context;
        private readonly Dictionary<string, IAgentTool> _offered;
        private readonly HashSet<string> _finals;

        // Spec 2026-11-23 D-14: cuántas llamadas a book_appointment devolvieron una cita en esta corrida. Un perfil es uno por corrida del
        // trabajo, así que un trabajo retomado vuelve a contar desde cero y encuentra la cita que ya hizo con la misma llave.
        private int _bookings;

        // Una escritura que tuvo éxito ya está hecha: descartar la respuesta por un turno nuevo del cliente dejaría que el siguiente trabajo
        // (otro JobId, otra llave) la repita.
        private bool _wrote;

        public bool Wrote => _wrote;

        public AgentProfile(AgentTurnContext context, IEnumerable<IAgentTool> tools)
        {
            _context = context;
            _offered = tools
                .Where(tool => AiServicePrincipal.Capabilities.Contains(tool.Capability) && tool.IsOfferedTo(context.Config.Tools, context.IsTest))
                .ToDictionary(tool => tool.Definition.Name, StringComparer.Ordinal);
            var definitions = _offered.Values.Select(tool => tool.Definition).Append(SendReply).Append(HandoffToHuman);
            // D-B8: sólo en modo bloque; fuera de él el modelo no la ve y, si la inventa, vuelve como herramienta desconocida.
            _finals = context.Block is null
                ? [AgentToolNames.SendReply, AgentToolNames.HandoffToHuman]
                : [AgentToolNames.SendReply, AgentToolNames.HandoffToHuman, AgentToolNames.FinishObjective];
            Tools = (context.Block is null ? definitions : definitions.Append(FinishObjective)).ToArray();
        }

        public IReadOnlyList<AiToolDefinition> Tools { get; }

        public bool IsFinal(string toolName) => _finals.Contains(toolName);

        public bool Writes(string toolName) => AgentToolNames.Writes(toolName);

        public async Task<ToolLoopExecution> ExecuteAsync(AiToolCall toolCall, JsonElement arguments, CancellationToken cancellationToken)
        {
            var result = await _offered[toolCall.Name].ExecuteAsync(
                new AgentToolInvocation(
                    _context.TenantId, _context.JobId, toolCall.Id, _context.CustomerId, _context.IsTest, _context.Config.QuotationDraft,
                    _context.AgentId, _context.ConversationId, BookingOrdinal: _bookings + 1),
                arguments,
                cancellationToken);
            // Una llamada que falla no consume número (D-14): si el modelo elige otra hora al reintentar, la llave sigue siendo la misma.
            if (toolCall.Name == AgentToolNames.BookAppointment && !result.IsError)
            {
                _bookings++;
            }

            if (AgentToolNames.Writes(toolCall.Name) && !result.IsError)
            {
                _wrote = true;
            }

            // P-4 y P-4b: una herramienta que escribió (o que no pudo por el cliente) termina el turno con su traspaso: lo que siga lo decide
            // una persona.
            return new ToolLoopExecution(
                result,
                result.Handoff is { } handoff ? new AgentHandedOff(handoff.Code, null, handoff.MemberId, handoff.QuotationNumber, Wrote: _wrote) : null);
        }

        /// <summary>La herramienta final, validada. <c>null</c> si no sirve tal como vino: cuenta como respuesta inválida.</summary>
        public AgentTurnOutcome? Finish(AiToolCall toolCall, IReadOnlySet<string> callIds)
        {
            JsonElement arguments;
            try
            {
                arguments = JsonDocument.Parse(toolCall.ArgumentsJson).RootElement;
            }
            catch (JsonException)
            {
                return null;
            }

            if (toolCall.Name == AgentToolNames.FinishObjective)
            {
                if (!AgentToolArguments.TryString(arguments, "summary", out var summary)
                    || summary.Trim().Length > AgentSession.ObjectiveSummaryMaxLength)
                {
                    return null;
                }

                return new AgentFinished(summary.Trim());
            }

            if (toolCall.Name == AgentToolNames.HandoffToHuman)
            {
                if (!AgentToolArguments.TryString(arguments, "reason", out var reason) || !AgentHandoffReasons.All.Contains(reason))
                {
                    return null;
                }

                var note = AgentToolArguments.TryString(arguments, "note", out var text) ? text.Trim() : null;
                return new AgentHandedOff(
                    reason, string.IsNullOrEmpty(note) ? null : note[..Math.Min(note.Length, AgentHandoffReasons.NoteMaxLength)], Wrote: _wrote);
            }

            if (!AgentToolArguments.TryString(arguments, "text", out var reply)
                || reply.Trim().Length is 0 or > ReplyMaxLength
                || LeaksInstructions(reply, _context.Config.Instructions))
            {
                return null;
            }

            var known = new HashSet<string>(_context.KnownSourceIds, StringComparer.Ordinal);
            known.UnionWith(callIds);
            var sources = arguments.TryGetProperty("sourceIds", out var ids) && ids.ValueKind == JsonValueKind.Array
                ? ids.EnumerateArray()
                    .Where(id => id.ValueKind == JsonValueKind.String)
                    .Select(id => id.GetString()!)
                    .Where(known.Contains)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
                : [];
            return new AgentReplied(reply.Trim(), sources, _wrote);
        }
    }
}
