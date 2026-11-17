using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.Extensions.Logging;
using Modules.Ai.Domain;

namespace Modules.Ai.Application;

/// <summary>Un trabajo del copiloto: el hilo y su dueño (el miembro con cuyos permisos corren las herramientas, D-C3) y la cuenta del
/// proveedor que eligió la configuración del tenant (C-19, sin respaldo en el otro, C-20).</summary>
public sealed record CopilotTurnContext(
    Guid TenantId,
    Guid UserId,
    Guid ThreadId,
    Guid JobId,
    AiProvider Provider,
    string Model,
    string? Effort,
    AiCredential Credential)
{
    public CopilotPrincipal Principal => new(TenantId, UserId);
}

/// <summary>El copiloto contestó: con <c>answer</c> (<paramref name="CannotAnswerReason"/> en <c>null</c>) o con <c>cannot_answer</c>, que
/// la pantalla muestra igual. <paramref name="Citations"/> ya salieron de los <c>ref</c> de este trabajo (D-C6).</summary>
public sealed record CopilotAnswered(string Text, IReadOnlyList<CopilotCitation> Citations, string? CannotAnswerReason = null) : AgentTurnOutcome;

/// <summary>El trabajo terminó sin respuesta por algo que no es del modelo (<c>member_inactive</c>).</summary>
public sealed record CopilotStopped(string Code) : AgentTurnOutcome;

/// <summary>D-C11: los topes de un trabajo del copiloto (<c>Ai:Copilot</c>), aparte de los del agente (<c>Ai:Model</c>). En
/// <see cref="AiRunLimits.MaxHistoryTurns"/>, los mensajes del hilo que viajan (D-C9).</summary>
public sealed record CopilotRunLimits(AiRunLimits Run);

/// <summary>
/// Plan fase 2, C2.2: el turno del copiloto sobre <see cref="ModelToolLoop"/>. Las herramientas son las que el miembro puede usar ahora
/// (<see cref="ICopilotToolExecutor"/>, revalidadas en cada llamada) más las finales <c>answer</c> y <c>cannot_answer</c>, todas ordenadas por
/// nombre para no romper el caché (C-8). Ninguna escribe. Antes de cada herramienta avisa el progreso (D-C7).
/// </summary>
public sealed class CopilotTurnRunner(
    IAiModelClientResolver resolver,
    IAiRunLedger ledger,
    IAiProviderCredentials credentials,
    ICopilotToolExecutor executor,
    CopilotRunLimits limits,
    IClock clock,
    ILogger<CopilotTurnRunner> logger,
    AiMetrics? metrics = null)
{
    public const int CannotAnswerMaxLength = 1000;

    /// <summary>Más citas que esto no ayudan a leer la respuesta; las de sobra se descartan.</summary>
    public const int MaxCitations = 20;

    /// <summary>§6: los motivos cerrados de <c>cannot_answer</c>.</summary>
    public static readonly IReadOnlyList<string> CannotAnswerReasons = ["no_tool", "no_permission", "no_data", "out_of_scope"];

    private static readonly AiToolDefinition Answer = new(
        CopilotToolNames.Answer,
        "Contesta la pregunta. Es la forma de terminar tu turno.",
        """
        {"type":"object","properties":{"text":{"type":"string","description":"La respuesta, máximo 8000 caracteres, en Markdown simple, sin HTML, sin imágenes y sin enlaces."},"citations":{"type":"array","items":{"type":"string"},"description":"Los id de los ref (pedidos, cotizaciones, clientes…) que devolvieron tus herramientas en este turno y en que te basaste."}},"required":["text","citations"],"additionalProperties":false}
        """);

    private static readonly AiToolDefinition CannotAnswer = new(
        CopilotToolNames.CannotAnswer,
        "Di que no puedes contestar y por qué. Termina tu turno.",
        """
        {"type":"object","properties":{"reason":{"type":"string","enum":["no_tool","no_permission","no_data","out_of_scope"],"description":"no_tool: no hay una herramienta o un reporte para eso; no_permission: quien pregunta no tiene acceso; no_data: no hay datos; out_of_scope: no es una pregunta sobre los datos del negocio."},"text":{"type":"string","description":"La explicación para quien pregunta, máximo 1000 caracteres."}},"required":["reason","text"],"additionalProperties":false}
        """);

    /// <summary>El <c>error_code</c> de una respuesta <c>Failed</c> (spec §5.2): el del proveedor, el del tope o <c>member_inactive</c>.</summary>
    public static string ErrorCodeOf(AgentTurnOutcome outcome) => outcome switch
    {
        CopilotStopped stopped => stopped.Code,
        AgentTurnFailed failed => failed.Code ?? AgentHandoffCodes.For(failed.Reason),
        AgentTurnRetry retry => retry.Code,
        _ => throw new InvalidOperationException($"{outcome.GetType().Name} is not a failure."),
    };

    /// <param name="onProgress">D-C7: el nombre de la herramienta que está por correr, para el mensaje pendiente.</param>
    public async Task<AgentTurnOutcome> RunAsync(
        CopilotTurnContext context, AgentPrompt prompt, Func<string, CancellationToken, Task> onProgress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(onProgress);
        var available = await executor.ListAvailableAsync(context.Principal, cancellationToken);
        if (available is null)
        {
            return new CopilotStopped(CopilotToolErrors.MemberInactive);
        }

        var run = new ToolLoopRun(
            context.TenantId, context.JobId, context.Provider, context.Model, context.Effort, context.Credential, limits.Run,
            new AiUsageOwner(null, null, null, null, null, context.ThreadId, context.UserId));
        return await new ModelToolLoop(resolver, ledger, credentials, clock, logger, metrics)
            .RunAsync(run, prompt, new CopilotProfile(context.Principal, available, executor, onProgress), cancellationToken);
    }

    private sealed class CopilotProfile(
        CopilotPrincipal principal,
        IReadOnlyList<AiToolDefinition> available,
        ICopilotToolExecutor executor,
        Func<string, CancellationToken, Task> onProgress) : IToolLoopProfile
    {
        // D-C6: lo citable de este trabajo, por id. La etiqueta también sale de acá, nunca del modelo.
        private readonly Dictionary<string, CopilotCitation> _refs = new(StringComparer.Ordinal);

        public IReadOnlyList<AiToolDefinition> Tools { get; } =
            [.. available.Where(tool => !IsFinalName(tool.Name)).Append(Answer).Append(CannotAnswer).OrderBy(tool => tool.Name, StringComparer.Ordinal)];

        public bool IsFinal(string toolName) => IsFinalName(toolName);

        public bool Writes(string toolName) => false;

        public async Task<ToolLoopExecution> ExecuteAsync(AiToolCall toolCall, JsonElement arguments, CancellationToken cancellationToken)
        {
            await onProgress(toolCall.Name, cancellationToken);
            var result = await executor.ExecuteAsync(principal, toolCall.Name, toolCall.ArgumentsJson, cancellationToken);
            if (!result.IsError)
            {
                foreach (var reference in result.Refs)
                {
                    _refs.TryAdd(reference.Id, reference);
                }
            }

            // D-C3: sin membresía activa no hay con qué seguir; el trabajo termina después de anotar la llamada.
            var ends = result.ErrorCode == CopilotToolErrors.MemberInactive ? new CopilotStopped(CopilotToolErrors.MemberInactive) : null;
            return new ToolLoopExecution(new AgentToolResult(result.ResultJson, result.IsError, result.ErrorCode), ends);
        }

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

            if (toolCall.Name == CopilotToolNames.CannotAnswer)
            {
                return AgentToolArguments.TryString(arguments, "reason", out var reason)
                    && CannotAnswerReasons.Contains(reason, StringComparer.Ordinal)
                    && AgentToolArguments.TryString(arguments, "text", out var explanation)
                    && explanation.Trim().Length is > 0 and <= CannotAnswerMaxLength
                        ? new CopilotAnswered(explanation.Trim(), [], reason)
                        : null;
            }

            if (!AgentToolArguments.TryString(arguments, "text", out var text) || text.Trim().Length is 0 or > CopilotThread.AnswerMaxLength)
            {
                return null;
            }

            // D-C6: sólo las que salieron de un ref de este trabajo; una inventada, repetida o con forma de URL no llega.
            var citations = arguments.TryGetProperty("citations", out var ids) && ids.ValueKind == JsonValueKind.Array
                ? ids.EnumerateArray()
                    .Where(id => id.ValueKind == JsonValueKind.String)
                    .Select(id => id.GetString()!)
                    .Distinct(StringComparer.Ordinal)
                    .Where(_refs.ContainsKey)
                    .Select(id => _refs[id])
                    .Take(MaxCitations)
                    .ToArray()
                : [];
            return new CopilotAnswered(text.Trim(), citations);
        }

        private static bool IsFinalName(string name) => name is CopilotToolNames.Answer or CopilotToolNames.CannotAnswer;
    }
}
