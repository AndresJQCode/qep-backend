using System.Diagnostics;
using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.Extensions.Logging;
using Modules.Ai.Domain;

namespace Modules.Ai.Application;

/// <summary>Quién paga una llamada al modelo en <c>ai.usage_records</c>: un agente (sesión o prueba) o un hilo del copiloto con el miembro
/// que preguntó (spec 2026-11-17 copiloto D-C12). El <c>CHECK</c> de la tabla exige uno de los dos.</summary>
public sealed record AiUsageOwner(
    Guid? AgentId,
    Guid? AgentVersionId,
    Guid? ConversationId,
    Guid? SessionId,
    Guid? TestId,
    Guid? CopilotThreadId = null,
    Guid? MemberUserId = null);

/// <summary>Un trabajo del bucle: la cuenta y el modelo de quien lo corre, sus topes y a quién se le anota el consumo. Nada aquí sale de
/// un argumento del modelo.</summary>
public sealed record ToolLoopRun(
    Guid TenantId,
    Guid JobId,
    AiProvider Provider,
    string Model,
    string? Effort,
    AiCredential Credential,
    AiRunLimits Limits,
    AiUsageOwner Owner);

/// <summary>El resultado de una herramienta. Con <paramref name="EndsTurn"/>, el bucle termina el turno con ese resultado después de
/// correr y anotar las demás llamadas de la vuelta, sin otra llamada al modelo (P-4 del agente).</summary>
public sealed record ToolLoopExecution(AgentToolResult Result, AgentTurnOutcome? EndsTurn = null);

/// <summary>Lo que distingue a quien corre el bucle (el agente, el copiloto): qué herramientas ofrece, cuáles terminan el turno y cómo se
/// validan, cuáles escriben y cómo se ejecuta cada una. Un perfil vive lo que dura un trabajo: puede guardar lo que sus herramientas
/// devolvieron (el copiloto valida sus citas contra eso).</summary>
public interface IToolLoopProfile
{
    /// <summary>Todas las que ve el modelo, finales incluidas, en el orden en que se mandan.</summary>
    IReadOnlyList<AiToolDefinition> Tools { get; }

    bool IsFinal(string toolName);

    /// <summary>A lo sumo una de éstas corre por respuesta del modelo.</summary>
    bool Writes(string toolName);

    /// <summary>La herramienta final validada; <c>null</c> si no sirve tal como vino (cuenta como respuesta inválida).</summary>
    /// <param name="callIds">Los ids de las llamadas de este trabajo.</param>
    AgentTurnOutcome? Finish(AiToolCall toolCall, IReadOnlySet<string> callIds);

    /// <summary>Una herramienta ofrecida que no es final. Un <see cref="JsonException"/> vuelve como <c>invalid_arguments</c> y cualquier
    /// otra excepción como <c>tool_failed</c>.</summary>
    Task<ToolLoopExecution> ExecuteAsync(AiToolCall toolCall, JsonElement arguments, CancellationToken cancellationToken);
}

/// <summary>
/// Plan fase 2, C2.1: el bucle común de herramientas, sacado de <see cref="AgentTurnRunner"/> sin cambiar su contrato. Pide al resolvedor
/// el adaptador del proveedor <b>del trabajo</b>, llama al modelo, ejecuta las herramientas del perfil y termina cuando el modelo llama una
/// final del perfil o se agota un tope.
/// <list type="bullet">
/// <item>C-10: cada vuelta se reenvía entera (los bloques opacos del proveedor) con todos sus resultados juntos; una herramienta que falla
/// vuelve con <c>is_error</c>, no se calla.</item>
/// <item>D-A27 y C-4: cada llamada deja su fila de consumo a nombre del dueño del trabajo, también las descartadas.</item>
/// <item>C-19: un error de la cuenta marca la conexión <b>de ese proveedor</b>. C-20: nunca reintenta con otro proveedor.</item>
/// </list>
/// No abre transacciones ni toma locks (D-A13).
/// </summary>
public sealed partial class ModelToolLoop(
    IAiModelClientResolver resolver,
    IAiRunLedger ledger,
    IAiProviderCredentials credentials,
    IClock clock,
    ILogger logger,
    AiMetrics? metrics = null)
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "AI job {JobId} of tenant {TenantId}: provider {Provider} failed with {Code} (request {RequestId}).")]
    private static partial void LogProviderFailed(ILogger logger, Guid tenantId, Guid jobId, string provider, string code, string? requestId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "AI job {JobId} of tenant {TenantId}: invalid model response ({Reason}).")]
    private static partial void LogInvalidResponse(ILogger logger, Guid tenantId, Guid jobId, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "AI job {JobId} of tenant {TenantId}: tool {Tool} threw.")]
    private static partial void LogToolThrew(ILogger logger, Exception exception, Guid tenantId, Guid jobId, string tool);

    [LoggerMessage(Level = LogLevel.Warning, Message = "AI job {JobId} of tenant {TenantId} stopped: {Reason}.")]
    private static partial void LogStopped(ILogger logger, Guid tenantId, Guid jobId, AgentFailureReason reason);

    public async Task<AgentTurnOutcome> RunAsync(ToolLoopRun run, AgentPrompt prompt, IToolLoopProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(profile);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(run.Limits.JobTimeout);
        try
        {
            return await LoopAsync(run, prompt, profile, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Stop(run, AgentFailureReason.JobTimedOut);
        }
    }

    private async Task<AgentTurnOutcome> LoopAsync(ToolLoopRun run, AgentPrompt prompt, IToolLoopProfile profile, CancellationToken cancellationToken)
    {
        var client = resolver.For(run.Provider);
        var definitions = profile.Tools.ToArray();
        var offered = definitions.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        var rounds = new List<AiModelRound>();
        var callIds = new HashSet<string>(StringComparer.Ordinal);
        var modelCalls = 0;
        var toolCalls = 0;
        var retriedInvalid = false;

        while (true)
        {
            if (modelCalls >= run.Limits.MaxModelCalls)
            {
                return Stop(run, AgentFailureReason.ModelCallsExceeded);
            }

            modelCalls++;
            var request = new AiModelRequest(
                run.Credential, run.Model, run.Effort, prompt.SystemPrompt, prompt.GreetingSent, prompt.VolatileContext,
                prompt.Messages, [.. rounds], definitions, run.Limits.MaxOutputTokens);
            AiModelResponse response;
            try
            {
                response = await client.CompleteAsync(request, cancellationToken);
            }
            catch (AiInvalidResponseException invalid)
            {
                await RecordUsageAsync(run, invalid.Usage, invalid.ServedModel, AiRunOutcomes.InvalidResponse);
                metrics?.RecordCall(run.Provider.Value, AiRunOutcomes.InvalidResponse);
                metrics?.RecordError(run.Provider.Value, invalid.Code);
                LogInvalidResponse(logger, run.TenantId, run.JobId, invalid.Reason);
                if (retriedInvalid)
                {
                    return Stop(run, AgentFailureReason.InvalidResponse);
                }

                retriedInvalid = true;
                continue;
            }
            catch (AiModelException failure)
            {
                var failed = failure is AiRefusedException ? AiRunOutcomes.Refused : AiRunOutcomes.Error;
                await RecordUsageAsync(run, failure.Usage, failure.ServedModel, failed);
                metrics?.RecordCall(run.Provider.Value, failed);
                metrics?.RecordError(run.Provider.Value, failure.Code);
                return await ProviderFailureAsync(run, failure, cancellationToken);
            }

            await RecordUsageAsync(run, response.Usage, response.ServedModel, AiRunOutcomes.Ok);
            metrics?.RecordCall(run.Provider.Value, AiRunOutcomes.Ok);

            var final = response.ToolCalls.FirstOrDefault(call => offered.Contains(call.Name) && profile.IsFinal(call.Name));
            if (final is not null)
            {
                var outcome = profile.Finish(final, callIds);
                if (outcome is not null)
                {
                    return outcome;
                }

                LogInvalidResponse(logger, run.TenantId, run.JobId, $"invalid {final.Name}");
                if (retriedInvalid)
                {
                    return Stop(run, AgentFailureReason.InvalidResponse);
                }

                retriedInvalid = true;
                continue;
            }

            toolCalls += response.ToolCalls.Count;
            if (toolCalls > run.Limits.MaxToolCalls)
            {
                return Stop(run, AgentFailureReason.ToolCallsExceeded);
            }

            var results = new List<AiToolResult>();
            AgentTurnOutcome? endsTurn = null;
            var wrote = false;
            foreach (var call in response.ToolCalls)
            {
                // Una sola herramienta que escribe por respuesta: dos create_quotation_draft en paralelo serían dos borradores. La segunda no
                // corre; vuelve con error y queda anotada.
                var writes = profile.Writes(call.Name);
                var execution = writes && wrote
                    ? await RejectAsync(run, call, AgentToolErrors.OneWritePerResponse, cancellationToken)
                    : await ExecuteAsync(run, profile, offered, call, cancellationToken);
                wrote |= writes;
                results.Add(new AiToolResult(call.Id, execution.Result.ResultJson, execution.Result.IsError));
                endsTurn ??= execution.EndsTurn;
                callIds.Add(call.Id);
            }

            // P-4 y P-4b: una herramienta que terminó el turno (un traspaso del agente) no le da otra vuelta al modelo. Las demás llamadas de
            // la vuelta ya corrieron y quedaron anotadas.
            if (endsTurn is not null)
            {
                return endsTurn;
            }

            rounds.Add(new AiModelRound(response.OpaqueAssistantBlocks, results));
        }
    }

    private async Task<ToolLoopExecution> ExecuteAsync(
        ToolLoopRun run, IToolLoopProfile profile, HashSet<string> offered, AiToolCall call, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        ToolLoopExecution execution;
        if (!offered.Contains(call.Name) || profile.IsFinal(call.Name))
        {
            execution = new ToolLoopExecution(AgentToolResult.Error(AgentToolErrors.UnknownTool));
        }
        else
        {
            try
            {
                using var arguments = JsonDocument.Parse(call.ArgumentsJson);
                execution = await profile.ExecuteAsync(call, arguments.RootElement, cancellationToken);
            }
            catch (JsonException)
            {
                execution = new ToolLoopExecution(AgentToolResult.Error(AgentToolErrors.InvalidArguments));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogToolThrew(logger, exception, run.TenantId, run.JobId, call.Name);
                execution = new ToolLoopExecution(AgentToolResult.Error(AgentToolErrors.ToolFailed));
            }
        }

        var result = execution.Result;
        await ledger.RecordToolCallAsync(
            new AiToolCallRecord(
                run.TenantId, run.JobId, call.Id, call.Name, call.ArgumentsJson, result.ResultJson,
                result.IsError ? AiRunOutcomes.Error : AiRunOutcomes.Ok, result.ErrorCode, (int)watch.ElapsedMilliseconds, clock.UtcNow),
            cancellationToken);
        return execution;
    }

    /// <summary>Una llamada que no se ejecuta: se anota con su error, como cualquier herramienta que falla.</summary>
    private async Task<ToolLoopExecution> RejectAsync(ToolLoopRun run, AiToolCall call, string errorCode, CancellationToken cancellationToken)
    {
        var result = AgentToolResult.Error(errorCode);
        await ledger.RecordToolCallAsync(
            new AiToolCallRecord(
                run.TenantId, run.JobId, call.Id, call.Name, call.ArgumentsJson, result.ResultJson, AiRunOutcomes.Error, errorCode, 0,
                clock.UtcNow),
            cancellationToken);
        return new ToolLoopExecution(result);
    }

    private async Task<AgentTurnOutcome> ProviderFailureAsync(ToolLoopRun run, AiModelException failure, CancellationToken cancellationToken)
    {
        LogProviderFailed(logger, run.TenantId, run.JobId, run.Provider.Value, failure.Code, failure.RequestId);
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
            await credentials.MarkNeedsAttentionAsync(run.TenantId, run.Credential.ConnectionId, connectionFailure, cancellationToken);
            return new AgentTurnFailed(AgentFailureKind.ProviderFailed, AgentFailureReason.ProviderError, failure.Code);
        }

        return new AgentTurnFailed(AgentFailureKind.AgentFailed, AgentFailureReason.ProviderError, failure.Code);
    }

    private AgentTurnFailed Stop(ToolLoopRun run, AgentFailureReason reason)
    {
        LogStopped(logger, run.TenantId, run.JobId, reason);
        return new AgentTurnFailed(AgentFailureKind.AgentFailed, reason, null);
    }

    private Task RecordUsageAsync(ToolLoopRun run, AiModelUsage? usage, string? servedModel, string outcome) =>
        usage is null
            ? Task.CompletedTask
            : ledger.RecordUsageAsync(
                new AiUsageRecord(
                    run.TenantId, run.Owner.AgentId, run.Owner.AgentVersionId, run.Owner.ConversationId, run.Owner.SessionId, run.Owner.TestId,
                    run.JobId, run.Provider, run.Model, servedModel, usage, outcome, clock.UtcNow, run.Owner.CopilotThreadId,
                    run.Owner.MemberUserId),
                // Lo gastado se registra aunque el trabajo se haya pasado de su tope.
                CancellationToken.None);
}
