using System.Diagnostics;
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
    bool IsTest);

/// <summary>Cómo terminó un trabajo. Quien lo corre decide qué hace con cada caso (enviar, traspasar, reintentar).</summary>
public abstract record AgentTurnOutcome;

/// <param name="SourceIds">Sólo las fuentes que existen (§8.2): las demás se descartan.</param>
public sealed record AgentReplied(string Text, IReadOnlyList<string> SourceIds) : AgentTurnOutcome;

/// <param name="Reason">Uno de <see cref="AgentHandoffReasons.All"/>, o el código de la herramienta que terminó el turno
/// (<see cref="AgentToolHandoff"/>).</param>
/// <param name="Note">Para el evento interno, nunca para el cliente.</param>
/// <param name="MemberId">P-4b: a quién va la conversación en vez del destino configurado (la asesora del borrador de cotización).</param>
public sealed record AgentHandedOff(string Reason, string? Note, Guid? MemberId = null) : AgentTurnOutcome;

/// <summary>Un error que el worker reintenta (D-A17): proveedor caído o límite de tasa.</summary>
public sealed record AgentTurnRetry(string Code, TimeSpan? RetryAfter) : AgentTurnOutcome;

/// <param name="Code">El código de la tabla de errores del plan si viene del proveedor; <c>null</c> en los topes de D-A20 y en la respuesta
/// inválida, que la tabla no nombra (el código del traspaso lo decide la Tarea 7).</param>
public sealed record AgentTurnFailed(AgentFailureKind Kind, AgentFailureReason Reason, string? Code) : AgentTurnOutcome;

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
public sealed record AiUsageRecord(
    Guid TenantId,
    Guid AgentId,
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
    DateTimeOffset CalledAt);

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
/// Plan 2026-11-12, 5.5: el bucle común de herramientas, igual para cualquier proveedor. Pide al resolvedor el adaptador del proveedor
/// <b>de la versión</b>, llama al modelo, ejecuta las herramientas de lectura y termina cuando el modelo llama una herramienta final
/// (<c>send_reply</c>, <c>handoff_to_human</c>) o se agota un tope de D-A20.
/// <list type="bullet">
/// <item>C-10: cada vuelta se reenvía entera (los bloques opacos del proveedor) con todos sus resultados juntos; una herramienta que falla
/// vuelve con <c>is_error</c>, no se calla.</item>
/// <item>D-A27 y C-4: cada llamada deja su fila de consumo, también las descartadas, con proveedor, caché y modelo servido.</item>
/// <item>C-19: un error de la cuenta (sin saldo, clave rechazada, sin permiso) marca la conexión <b>de ese proveedor</b>; los agentes del
/// otro no se enteran.</item>
/// <item>C-20: nunca reintenta con otro proveedor.</item>
/// </list>
/// No abre transacciones ni toma locks: eso es del commit corto de quien lo corre (D-A13).
/// </summary>
public sealed partial class AgentTurnRunner(
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "AI job {JobId} of tenant {TenantId}: provider {Provider} failed with {Code} (request {RequestId}).")]
    private static partial void LogProviderFailed(ILogger logger, Guid tenantId, Guid jobId, string provider, string code, string? requestId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "AI job {JobId} of tenant {TenantId}: invalid model response ({Reason}).")]
    private static partial void LogInvalidResponse(ILogger logger, Guid tenantId, Guid jobId, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "AI job {JobId} of tenant {TenantId}: tool {Tool} threw.")]
    private static partial void LogToolThrew(ILogger logger, Exception exception, Guid tenantId, Guid jobId, string tool);

    [LoggerMessage(Level = LogLevel.Warning, Message = "AI job {JobId} of tenant {TenantId} stopped: {Reason}.")]
    private static partial void LogStopped(ILogger logger, Guid tenantId, Guid jobId, AgentFailureReason reason);

    public async Task<AgentTurnOutcome> RunAsync(AgentTurnContext context, AgentPrompt prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(prompt);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limits.JobTimeout);
        try
        {
            return await LoopAsync(context, prompt, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Stop(context, AgentFailureReason.JobTimedOut);
        }
    }

    private async Task<AgentTurnOutcome> LoopAsync(AgentTurnContext context, AgentPrompt prompt, CancellationToken cancellationToken)
    {
        var client = resolver.For(context.Provider);
        var offered = tools
            .Where(tool => AiServicePrincipal.Capabilities.Contains(tool.Capability) && tool.IsOfferedTo(context.Config.Tools, context.IsTest))
            .ToDictionary(tool => tool.Definition.Name, StringComparer.Ordinal);
        var definitions = offered.Values.Select(tool => tool.Definition).Append(SendReply).Append(HandoffToHuman).ToArray();
        var rounds = new List<AiModelRound>();
        var callIds = new HashSet<string>(StringComparer.Ordinal);
        var modelCalls = 0;
        var toolCalls = 0;
        var retriedInvalid = false;

        while (true)
        {
            if (modelCalls >= limits.MaxModelCalls)
            {
                return Stop(context, AgentFailureReason.ModelCallsExceeded);
            }

            modelCalls++;
            var request = new AiModelRequest(
                context.Credential, context.Model, context.Effort, prompt.SystemPrompt, prompt.GreetingSent, prompt.VolatileContext,
                prompt.Messages, [.. rounds], definitions, limits.MaxOutputTokens);
            AiModelResponse response;
            try
            {
                response = await client.CompleteAsync(request, cancellationToken);
            }
            catch (AiInvalidResponseException invalid)
            {
                await RecordUsageAsync(context, invalid.Usage, invalid.ServedModel, AiRunOutcomes.InvalidResponse);
                metrics?.RecordCall(context.Provider.Value, AiRunOutcomes.InvalidResponse);
                metrics?.RecordError(context.Provider.Value, invalid.Code);
                LogInvalidResponse(logger, context.TenantId, context.JobId, invalid.Reason);
                if (retriedInvalid)
                {
                    return Stop(context, AgentFailureReason.InvalidResponse);
                }

                retriedInvalid = true;
                continue;
            }
            catch (AiModelException failure)
            {
                var failed = failure is AiRefusedException ? AiRunOutcomes.Refused : AiRunOutcomes.Error;
                await RecordUsageAsync(context, failure.Usage, failure.ServedModel, failed);
                metrics?.RecordCall(context.Provider.Value, failed);
                metrics?.RecordError(context.Provider.Value, failure.Code);
                return await ProviderFailureAsync(context, failure, cancellationToken);
            }

            await RecordUsageAsync(context, response.Usage, response.ServedModel, AiRunOutcomes.Ok);
            metrics?.RecordCall(context.Provider.Value, AiRunOutcomes.Ok);

            var final = response.ToolCalls.FirstOrDefault(call =>
                call.Name is AgentToolNames.SendReply or AgentToolNames.HandoffToHuman);
            if (final is not null)
            {
                var outcome = Finish(context, final, callIds);
                if (outcome is not null)
                {
                    return outcome;
                }

                LogInvalidResponse(logger, context.TenantId, context.JobId, $"invalid {final.Name}");
                if (retriedInvalid)
                {
                    return Stop(context, AgentFailureReason.InvalidResponse);
                }

                retriedInvalid = true;
                continue;
            }

            toolCalls += response.ToolCalls.Count;
            if (toolCalls > limits.MaxToolCalls)
            {
                return Stop(context, AgentFailureReason.ToolCallsExceeded);
            }

            var results = new List<AiToolResult>();
            AgentToolHandoff? handoff = null;
            var wrote = false;
            foreach (var call in response.ToolCalls)
            {
                // Una sola herramienta que escribe por respuesta: dos create_quotation_draft en paralelo serían dos borradores. La segunda no
                // corre; vuelve con error y queda anotada.
                var writes = AgentToolNames.Writes(call.Name);
                var (result, requested) = writes && wrote
                    ? await RejectAsync(context, call, AgentToolErrors.OneWritePerResponse, cancellationToken)
                    : await ExecuteAsync(context, offered, call, cancellationToken);
                wrote |= writes;
                results.Add(result);
                handoff ??= requested;
                callIds.Add(call.Id);
            }

            // P-4 y P-4b: una herramienta que escribió (o que no pudo por el cliente) termina el turno con su traspaso, sin otra vuelta del
            // modelo: lo que siga lo decide una persona. Las demás llamadas de la vuelta ya corrieron y quedaron anotadas.
            if (handoff is not null)
            {
                return new AgentHandedOff(handoff.Code, null, handoff.MemberId);
            }

            rounds.Add(new AiModelRound(response.OpaqueAssistantBlocks, results));
        }
    }

    /// <summary>La herramienta final, validada. <c>null</c> si no sirve tal como vino: cuenta como respuesta inválida.</summary>
    private static AgentTurnOutcome? Finish(AgentTurnContext context, AiToolCall call, HashSet<string> callIds)
    {
        JsonElement arguments;
        try
        {
            arguments = JsonDocument.Parse(call.ArgumentsJson).RootElement;
        }
        catch (JsonException)
        {
            return null;
        }

        if (call.Name == AgentToolNames.HandoffToHuman)
        {
            if (!AgentToolArguments.TryString(arguments, "reason", out var reason) || !AgentHandoffReasons.All.Contains(reason))
            {
                return null;
            }

            var note = AgentToolArguments.TryString(arguments, "note", out var text) ? text.Trim() : null;
            return new AgentHandedOff(reason, string.IsNullOrEmpty(note) ? null : note[..Math.Min(note.Length, AgentHandoffReasons.NoteMaxLength)]);
        }

        if (!AgentToolArguments.TryString(arguments, "text", out var reply)
            || reply.Trim().Length is 0 or > ReplyMaxLength
            || LeaksInstructions(reply, context.Config.Instructions))
        {
            return null;
        }

        var known = new HashSet<string>(context.KnownSourceIds, StringComparer.Ordinal);
        known.UnionWith(callIds);
        var sources = arguments.TryGetProperty("sourceIds", out var ids) && ids.ValueKind == JsonValueKind.Array
            ? ids.EnumerateArray()
                .Where(id => id.ValueKind == JsonValueKind.String)
                .Select(id => id.GetString()!)
                .Where(known.Contains)
                .Distinct(StringComparer.Ordinal)
                .ToArray()
            : [];
        return new AgentReplied(reply.Trim(), sources);
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

    private async Task<(AiToolResult Result, AgentToolHandoff? Handoff)> ExecuteAsync(
        AgentTurnContext context, Dictionary<string, IAgentTool> offered, AiToolCall call, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        AgentToolResult result;
        if (!offered.TryGetValue(call.Name, out var tool))
        {
            result = AgentToolResult.Error(AgentToolErrors.UnknownTool);
        }
        else
        {
            try
            {
                using var arguments = JsonDocument.Parse(call.ArgumentsJson);
                result = await tool.ExecuteAsync(
                    new AgentToolInvocation(
                        context.TenantId, context.JobId, call.Id, context.CustomerId, context.IsTest, context.Config.QuotationDraft),
                    arguments.RootElement,
                    cancellationToken);
            }
            catch (JsonException)
            {
                result = AgentToolResult.Error(AgentToolErrors.InvalidArguments);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogToolThrew(logger, exception, context.TenantId, context.JobId, call.Name);
                result = AgentToolResult.Error(AgentToolErrors.ToolFailed);
            }
        }

        await ledger.RecordToolCallAsync(
            new AiToolCallRecord(
                context.TenantId, context.JobId, call.Id, call.Name, call.ArgumentsJson, result.ResultJson,
                result.IsError ? AiRunOutcomes.Error : AiRunOutcomes.Ok, result.ErrorCode, (int)watch.ElapsedMilliseconds, clock.UtcNow),
            cancellationToken);
        return (new AiToolResult(call.Id, result.ResultJson, result.IsError), result.Handoff);
    }

    /// <summary>Una llamada que no se ejecuta: se anota con su error, como cualquier herramienta que falla.</summary>
    private async Task<(AiToolResult Result, AgentToolHandoff? Handoff)> RejectAsync(
        AgentTurnContext context, AiToolCall call, string errorCode, CancellationToken cancellationToken)
    {
        var result = AgentToolResult.Error(errorCode);
        await ledger.RecordToolCallAsync(
            new AiToolCallRecord(
                context.TenantId, context.JobId, call.Id, call.Name, call.ArgumentsJson, result.ResultJson, AiRunOutcomes.Error, errorCode, 0,
                clock.UtcNow),
            cancellationToken);
        return (new AiToolResult(call.Id, result.ResultJson, true), null);
    }

    private async Task<AgentTurnOutcome> ProviderFailureAsync(
        AgentTurnContext context, AiModelException failure, CancellationToken cancellationToken)
    {
        LogProviderFailed(logger, context.TenantId, context.JobId, context.Provider.Value, failure.Code, failure.RequestId);
        if (failure.IsRetryable)
        {
            return new AgentTurnRetry(failure.Code, (failure as AiRateLimitedException)?.RetryAfter);
        }

        // C-19: la cuenta de este proveedor queda en NeedsAttention con el motivo; una prueba exitosa de la clave la saca (Integrations).
        var connectionFailure = failure switch
        {
            AiQuotaExhaustedException => AiCredentialFailures.QuotaExhausted,
            AiCredentialsRejectedException => AiCredentialFailures.CredentialsRejected,
            AiPermissionDeniedException => AiCredentialFailures.PermissionDenied,
            _ => null,
        };
        if (connectionFailure is not null)
        {
            await credentials.MarkNeedsAttentionAsync(context.TenantId, context.Credential.ConnectionId, connectionFailure, cancellationToken);
            return new AgentTurnFailed(AgentFailureKind.ProviderFailed, AgentFailureReason.ProviderError, failure.Code);
        }

        return new AgentTurnFailed(AgentFailureKind.AgentFailed, AgentFailureReason.ProviderError, failure.Code);
    }

    private AgentTurnFailed Stop(AgentTurnContext context, AgentFailureReason reason)
    {
        LogStopped(logger, context.TenantId, context.JobId, reason);
        return new AgentTurnFailed(AgentFailureKind.AgentFailed, reason, null);
    }

    private Task RecordUsageAsync(
        AgentTurnContext context, AiModelUsage? usage, string? servedModel, string outcome) =>
        usage is null
            ? Task.CompletedTask
            : ledger.RecordUsageAsync(
                new AiUsageRecord(
                    context.TenantId, context.AgentId, context.AgentVersionId, context.ConversationId, context.SessionId, context.TestId,
                    context.JobId, context.Provider, context.Model, servedModel, usage, outcome, clock.UtcNow),
                // Lo gastado se registra aunque el trabajo se haya pasado de su tope.
                CancellationToken.None);
}
