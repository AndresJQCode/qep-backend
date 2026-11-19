using System.Globalization;
using System.Net;
using System.Text.Json;
using Anthropic;
using Anthropic.Core;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Ai.Application;
using Modules.Ai.Infrastructure.Options;

namespace Modules.Ai.Infrastructure.Model.Anthropic;

/// <summary>
/// Plan 2026-11-12, 5.3: <see cref="IAiModelClient"/> sobre la Messages API de Anthropic con el SDK oficial (<c>Anthropic</c> 13.x),
/// por el endpoint beta y sin streaming (C-6). Todas las reglas que dependen de Claude viven aquí; el bucle común no sabe de ellas.
/// <para>Las tres que el siguiente va a querer «simplificar», y por qué no:</para>
/// <list type="bullet">
/// <item><b>C-8, sin herramienta forzada.</b> En Opus 5.5 y Sonnet 5.5 un <c>tool_choice</c> <c>any</c> o <c>tool</c> responde 400. Va
/// <c>auto</c> (el default, sin mandarlo) con <c>strict: true</c>, y una vuelta que termina en <c>end_turn</c> con texto se devuelve como
/// <c>send_reply</c> sin fuentes: el bucle común la recibe ya normalizada.</item>
/// <item><b>C-11, nunca termina en <c>assistant</c>.</b> Un <c>assistant</c> al final es <i>prefill</i> y responde 400. El saludo ya enviado
/// va en un mensaje <c>system</c> después del primer turno del cliente, y la petición termina en <c>user</c> o en el <c>system</c> del
/// contexto volátil.</item>
/// <item><b>C-13, el SDK no reintenta.</b> <c>MaxRetries = 0</c>: con los reintentos del SDK el tiempo real sería <c>timeout × 3</c> y el
/// trabajo pasaría su tope y su lease sin saberlo. Los reintentos son del worker (D-A17), iguales para los dos proveedores.</item>
/// </list>
/// <para>C-14: un cliente del SDK por llamada, con la clave de la credencial de la petición, descartado al terminar; nunca uno global con
/// clave. El <see cref="HttpClient"/> sale del cliente nombrado <see cref="HttpClientName"/> (sin loggers, sin redirecciones). Con el SDK
/// 13.x la clave viaja como <c>Authorization: Bearer</c>, no como <c>x-api-key</c> (documentación de <c>ClientOptions.ApiKey</c>).</para>
/// <para>D-A29: las trazas del SDK van en <see cref="OpenTelemetryContentMode.MetadataOnly"/>, fijado explícito aunque sea el default. El
/// log lleva ids, modelo, status y el tipo del error; nunca el cuerpo, la clave ni el texto.</para>
/// </summary>
public sealed partial class ClaudeModelClient(
    IHttpMessageHandlerFactory handlers, IOptions<AiModelCallOptions> options, ILogger<ClaudeModelClient> logger) : IAiModelClient
{
    /// <summary>El cliente nombrado del adaptador (<c>RemoveAllLoggers</c>): el log de <see cref="HttpClient"/> escribiría los headers.</summary>
    public const string HttpClientName = "ai.anthropic";

    /// <summary>C-9: el respaldo por rechazo dentro de Anthropic, en beta.</summary>
    internal const string FallbackBeta = "server-side-fallback-2026-07-01";

    /// <summary>C-8: la herramienta final en que se convierte un <c>end_turn</c> con texto. Es la misma que declara el bucle común.</summary>
    internal const string SendReplyTool = "send_reply";

    /// <summary>C-9: Haiku 5.5 no tiene respaldo por rechazo; Opus 5.5 y Sonnet 5.5 sí.</summary>
    private static readonly string[] ModelsWithFallback = ["claude-opus-", "claude-sonnet-"];

    [LoggerMessage(Level = LogLevel.Warning, Message = "Anthropic rejected request {RequestId} for model {Model} with status {Status} and error type {ErrorType}.")]
    private static partial void LogRejected(ILogger logger, string? requestId, string model, int status, string? errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Anthropic call for model {Model} failed before a response ({Failure}).")]
    private static partial void LogUnreachable(ILogger logger, string model, string failure);

    [LoggerMessage(Level = LogLevel.Information, Message = "Anthropic request {RequestId} for model {Model} ended with {StopReason}; {Input} input and {Output} output tokens.")]
    private static partial void LogCompleted(ILogger logger, string? requestId, string model, string? stopReason, long input, long output);

    public async Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var capture = new ResponseHeadersCapture { InnerHandler = handlers.CreateHandler(HttpClientName) };
        // El handler es del pool de IHttpClientFactory: ni el HttpClient ni la captura lo desechan.
        using var http = new HttpClient(capture, disposeHandler: false);
        var client = new AnthropicClient
        {
            ApiKey = request.Credential.ApiKey,
            HttpClient = http,
            MaxRetries = 0,
            Timeout = TimeSpan.FromSeconds(options.Value.CallTimeoutSeconds),
            OpenTelemetry = new() { Traces = new() { ContentMode = OpenTelemetryContentMode.MetadataOnly } },
        };

        BetaMessage message;
        try
        {
            message = await client.Beta.Messages.Create(BuildParams(request), cancellationToken);
        }
        catch (AnthropicApiException exception)
        {
            var errorType = ErrorTypeOf(exception.ResponseBody);
            var status = (int)exception.StatusCode;
            LogRejected(logger, capture.RequestId, request.Model, status, errorType);
            throw Translate(status, errorType, capture, exception);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is AnthropicIOException or HttpRequestException or OperationCanceledException)
        {
            // Red caída o el timeout de la llamada (no una cancelación de quien llama).
            LogUnreachable(logger, request.Model, exception.GetType().Name);
            throw new AiProviderUnavailableException(capture.RequestId, exception);
        }

        var requestId = capture.RequestId ?? message.ID;
        var usage = new AiModelUsage(
            message.Usage.InputTokens, message.Usage.OutputTokens, message.Usage.CacheCreationInputTokens, message.Usage.CacheReadInputTokens);
        string servedModel = message.Model;
        var stopReason = message.StopReason?.Raw();
        LogCompleted(logger, requestId, servedModel, stopReason, usage.Input, usage.Output);

        if (stopReason == "refusal")
        {
            throw new AiRefusedException(message.StopDetails?.Category?.Raw(), requestId) { Usage = usage, ServedModel = servedModel };
        }

        if (stopReason == "max_tokens")
        {
            throw new AiInvalidResponseException("max_tokens", requestId) { Usage = usage, ServedModel = servedModel };
        }

        return ToResponse(message, stopReason, usage, servedModel, requestId);
    }

    private static MessageCreateParams BuildParams(AiModelRequest request)
    {
        var parameters = new MessageCreateParams
        {
            Model = request.Model,
            MaxTokens = request.MaxOutputTokens,
            // C-12: system estable con un solo punto de caché al final, y caché automático del historial a nivel superior.
            System = new List<BetaTextBlockParam> { new() { Text = request.SystemPrompt, CacheControl = new BetaCacheControlEphemeral() } },
            CacheControl = new BetaCacheControlEphemeral(),
            // C-12: orden por nombre, para que el prefijo cacheado no dependa del orden en que se armaron.
            Tools = [.. request.Tools.OrderBy(tool => tool.Name, StringComparer.Ordinal).Select(ToTool)],
            Messages = BuildMessages(request),
        };

        // C-7: sin Thinking (los tres modelos corren adaptativo; en Opus 5.5 apagarlo es 400). La profundidad es el esfuerzo, siempre
        // explícito desde la lista curada. Lo opcional se agrega sólo si va: el SDK manda un null explícito si se asigna.
        if (request.Effort is { } effort)
        {
            parameters = parameters with { OutputConfig = new BetaOutputConfig { Effort = EffortOf(effort) } };
        }

        if (ModelsWithFallback.Any(prefix => request.Model.StartsWith(prefix, StringComparison.Ordinal)))
        {
            parameters = parameters with { Betas = [FallbackBeta], Fallbacks = new Default() };
        }

        return parameters;
    }

    /// <summary>
    /// C-11 y C-10: el historial (alternado, termina en <c>user</c>), el saludo como <c>system</c> después del primer turno del cliente, el
    /// contexto volátil como <c>system</c> al final del historial, y después las vueltas de este trabajo, append-only: el <c>system</c>
    /// volátil queda donde estaba en la primera llamada para no editar lo que ya vio el modelo (los bloques de pensamiento sólo valen sobre
    /// el historial sin cambios). Dos <c>system</c> seguidos no se mandan: con un solo turno del cliente, saludo y contexto van juntos.
    /// </summary>
    private static List<BetaMessageParam> BuildMessages(AiModelRequest request)
    {
        var messages = new List<BetaMessageParam>();
        var firstUser = true;
        string? pendingSystem = null;
        foreach (var message in request.Messages)
        {
            if (pendingSystem is not null)
            {
                messages.Add(System(pendingSystem));
                pendingSystem = null;
            }

            if (message.Role == AiChatRole.User)
            {
                messages.Add(new BetaUserMessageParam { Content = message.Text });
                if (firstUser && request.GreetingSent is { } greeting)
                {
                    pendingSystem = $"Al abrir la conversación ya le enviaste al cliente este saludo: «{greeting}». No lo repitas.";
                }

                firstUser = false;
            }
            else
            {
                messages.Add(new BetaAssistantMessageParam { Content = message.Text });
            }
        }

        var volatileContext = string.IsNullOrWhiteSpace(request.VolatileContext) ? null : request.VolatileContext;
        var tail = string.Join("\n\n", new[] { pendingSystem, volatileContext }.OfType<string>());
        if (tail.Length > 0)
        {
            messages.Add(System(tail));
        }

        foreach (var round in request.PriorAssistantBlocks)
        {
            messages.Add(new BetaAssistantMessageParam
            {
                Content = round.AssistantBlocks.Select(block => OpaqueBlocks.ToParam(block)).ToList(),
            });
            // C-10: todos los resultados de una vuelta en un solo mensaje, con is_error el que falló.
            messages.Add(new BetaUserMessageParam
            {
                Content = round.ToolResults
                    .Select(result => (BetaContentBlockParam)new BetaToolResultBlockParam
                    {
                        ToolUseID = result.CallId,
                        Content = result.ContentJson,
                        IsError = result.IsError ? true : null,
                    })
                    .ToList(),
            });
        }

        return messages;
    }

    private static BetaSystemMessageParam System(string text) => new() { Content = text };

    private static BetaToolUnion ToTool(AiToolDefinition tool) =>
        new BetaTool
        {
            Name = tool.Name,
            Description = tool.Description,
            // C-8: strict con additionalProperties false y todo en required, como lo declara el esquema neutral. Tope de Anthropic: 20
            // estrictas por request; las de lectura del copiloto se declaran no estrictas (AiToolDefinition.Strict).
            Strict = tool.Strict,
            InputSchema = InputSchema.FromRawUnchecked(
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(tool.InputSchemaJson)
                ?? throw new InvalidOperationException($"The schema of tool '{tool.Name}' is empty.")),
        };

    private static Effort EffortOf(string effort) => effort switch
    {
        "low" => Effort.Low,
        "medium" => Effort.Medium,
        "high" => Effort.High,
        "xhigh" => Effort.Xhigh,
        "max" => Effort.Max,
        _ => throw new InvalidOperationException($"'{effort}' is not an Anthropic effort level; AiOptions validates the list at startup."),
    };

    private static AiModelResponse ToResponse(
        BetaMessage message, string? stopReason, AiModelUsage usage, string servedModel, string? requestId)
    {
        var calls = new List<AiToolCall>();
        var opaque = new List<AiOpaqueBlock>();
        var texts = new List<string>();
        foreach (var block in message.Content)
        {
            if (block.TryPickToolUse(out var toolUse))
            {
                calls.Add(new AiToolCall(toolUse.ID, toolUse.Name, JsonSerializer.Serialize(toolUse.Input)));
            }
            else if (block.TryPickText(out var text))
            {
                texts.Add(text.Text);
            }

            if (OpaqueBlocks.FromResponse(block) is { } kept)
            {
                opaque.Add(kept);
            }
        }

        var joined = texts.Count == 0 ? null : string.Join("\n\n", texts);
        if (calls.Count == 0)
        {
            // C-8: sin herramienta forzada, un end_turn con texto es la respuesta al cliente, sin fuentes.
            if (stopReason == "end_turn" && !string.IsNullOrWhiteSpace(joined))
            {
                calls.Add(new AiToolCall(
                    $"{message.ID}:end_turn",
                    SendReplyTool,
                    JsonSerializer.Serialize(new { text = joined, sourceIds = Array.Empty<string>() })));
                return new AiModelResponse(joined, calls, opaque, AiStopReason.EndTurn, usage, servedModel, requestId);
            }

            throw new AiInvalidResponseException(stopReason ?? "empty", requestId) { Usage = usage, ServedModel = servedModel };
        }

        return new AiModelResponse(joined, calls, opaque, AiStopReason.ToolUse, usage, servedModel, requestId);
    }

    /// <summary>Por el <c>type</c> del error, nunca por el texto; el status sólo cuando el cuerpo no trae tipo.</summary>
    private static AiModelException Translate(int status, string? errorType, ResponseHeadersCapture capture, Exception inner) =>
        (errorType, status) switch
        {
            ("authentication_error", _) or (null, 401) => new AiCredentialsRejectedException(capture.RequestId, inner),
            ("permission_error", _) or (null, 403) => new AiPermissionDeniedException(capture.RequestId, inner),
            ("billing_error", _) or (null, 402) => new AiQuotaExhaustedException(capture.RequestId, inner),
            ("not_found_error", _) or (null, 404) => new AiModelUnavailableException(capture.RequestId, inner),
            ("rate_limit_error", _) or (null, 429) => new AiRateLimitedException(capture.RetryAfter, capture.RequestId, inner),
            ("invalid_request_error" or "request_too_large", _) or (null, 400 or 413) => new AiInvalidRequestException(capture.RequestId, inner),
            ("api_error" or "overloaded_error" or "timeout_error", _) => new AiProviderUnavailableException(capture.RequestId, inner),
            (_, >= 500) => new AiProviderUnavailableException(capture.RequestId, inner),
            // Un 4xx con un tipo que no está en la tabla: la petición no sirve tal como está, reintentarla no cambia nada.
            _ => new AiInvalidRequestException(capture.RequestId, inner),
        };

    private static string? ErrorTypeOf(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                    ? type.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// El SDK no expone los headers de la respuesta en su excepción: esta captura anota el <c>request-id</c> (para el log) y el
    /// <c>retry-after</c> (para <see cref="AiRateLimitedException"/>) de la última respuesta. Nada más: ni cuerpo ni headers de la petición.
    /// </summary>
    private sealed class ResponseHeadersCapture : DelegatingHandler
    {
        public string? RequestId { get; private set; }

        public TimeSpan? RetryAfter { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            RequestId = response.Headers.TryGetValues("request-id", out var ids) ? ids.FirstOrDefault() : null;
            RetryAfter = RetryAfterOf(response);
            return response;
        }

        private static TimeSpan? RetryAfterOf(HttpResponseMessage response)
        {
            if (response.StatusCode != HttpStatusCode.TooManyRequests)
            {
                return null;
            }

            if (response.Headers.RetryAfter?.Delta is { } delta)
            {
                return delta;
            }

            return response.Headers.TryGetValues("retry-after", out var values)
                && double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                    ? TimeSpan.FromSeconds(seconds)
                    : null;
        }
    }
}
