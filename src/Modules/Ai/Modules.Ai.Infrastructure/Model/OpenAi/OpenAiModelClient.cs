using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Ai.Application;
using Modules.Ai.Infrastructure.Options;

namespace Modules.Ai.Infrastructure.Model.OpenAi;

/// <summary>
/// Spec 2026-10-29 §9.1 (P-1 para OpenAI): <see cref="IAiModelClient"/> sobre la Responses API de OpenAI (O-1), con
/// <see cref="HttpClient"/> directo y sin streaming (O-2). Todas las reglas que dependen de OpenAI viven aquí; el bucle común no sabe de
/// ellas.
/// <para>Las que el siguiente va a querer «simplificar», y por qué no:</para>
/// <list type="bullet">
/// <item><b>O-3, <c>store: false</c> siempre.</b> Omitido vale <c>true</c> y la conversación del cliente quedaría guardada en la cuenta del
/// tenant. Sin estado, el razonamiento de una vuelta sólo sirve en la siguiente si viaja cifrado: por eso el <c>include</c>.</item>
/// <item><b>O-4, los ítems de salida vuelven tal cual.</b> El <c>encrypted_content</c> de un ítem de razonamiento sólo vale sin tocar, y
/// <c>phase</c> se reenvía en los modelos que lo traen. Se guarda el JSON del ítem completo, no una versión rearmada.</item>
/// <item><b>C-13, nadie reintenta aquí.</b> Sin SDK no hay reintentos escondidos; el timeout del <see cref="HttpClient"/> es el de la
/// llamada (<c>Ai:Model:CallTimeoutSeconds</c>). Los reintentos son del worker (D-A17), iguales para los dos proveedores.</item>
/// </list>
/// <para>C-14: la clave va en el <c>Authorization</c> de cada petición, nunca en <c>DefaultRequestHeaders</c>, y el handler sale del cliente
/// nombrado <see cref="HttpClientName"/> (sin loggers, sin redirecciones). D-A29: el log lleva status, tipo y código del error, modelo e
/// id de la petición; nunca el cuerpo, la clave ni el texto.</para>
/// </summary>
public sealed partial class OpenAiModelClient(
    IHttpMessageHandlerFactory handlers, IOptions<AiModelCallOptions> options, ILogger<OpenAiModelClient> logger) : IAiModelClient
{
    /// <summary>El cliente nombrado del adaptador (<c>RemoveAllLoggers</c>): el log de <see cref="HttpClient"/> escribiría los headers.</summary>
    public const string HttpClientName = "ai.openai";

    /// <summary>C-8: la herramienta final en que se convierte una respuesta con sólo texto. Es la misma que declara el bucle común.</summary>
    internal const string SendReplyTool = "send_reply";

    private static readonly Uri ResponsesUri = new("https://api.openai.com/v1/responses");

    /// <summary>O-9: los 429 que son de saldo o de límite de gasto, por <c>error.code</c> (guía de códigos de error). Reintentarlos no devuelve
    /// el acceso.</summary>
    private static readonly string[] QuotaCodes =
    [
        "insufficient_quota",
        "credit_balance_exhausted",
        "organization_spend_limit_exceeded",
        "project_spend_limit_exceeded",
        "organization_usage_limit_exceeded",
    ];

    [LoggerMessage(Level = LogLevel.Warning, Message = "OpenAI rejected request {RequestId} for model {Model} with status {Status}, error type {ErrorType} and code {ErrorCode}.")]
    private static partial void LogRejected(ILogger logger, string? requestId, string model, int status, string? errorType, string? errorCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "OpenAI call for model {Model} failed before a response ({Failure}).")]
    private static partial void LogUnreachable(ILogger logger, string model, string failure);

    [LoggerMessage(Level = LogLevel.Information, Message = "OpenAI request {RequestId} for model {Model} ended with status {Status}; {Input} input and {Output} output tokens.")]
    private static partial void LogCompleted(ILogger logger, string? requestId, string model, string? status, long input, long output);

    public async Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        // El handler es del pool de IHttpClientFactory: el HttpClient no lo desecha.
        using var http = new HttpClient(handlers.CreateHandler(HttpClientName), disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(options.Value.CallTimeoutSeconds),
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, ResponsesUri)
        {
            Content = new StringContent(BuildBody(request).ToJsonString(), Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.Credential.ApiKey);

        string? requestId = null;
        string body;
        try
        {
            using var response = await http.SendAsync(message, cancellationToken);
            requestId = response.Headers.TryGetValues("x-request-id", out var ids) ? ids.FirstOrDefault() : null;
            body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var (errorType, errorCode) = ErrorOf(body);
                var status = (int)response.StatusCode;
                LogRejected(logger, requestId, request.Model, status, errorType, errorCode);
                throw Translate(status, errorType, errorCode, RetryAfterOf(response), requestId);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            // Red caída o el timeout de la llamada (no una cancelación de quien llama).
            LogUnreachable(logger, request.Model, exception.GetType().Name);
            throw new AiProviderUnavailableException(requestId, exception);
        }

        return Read(body, request.Model, requestId);
    }

    private static JsonObject BuildBody(AiModelRequest request)
    {
        var body = new JsonObject
        {
            ["model"] = request.Model,
            // O-5: lo estable en instructions; lo volátil va al final del input, fuera del prefijo cacheable.
            ["instructions"] = request.SystemPrompt,
            ["input"] = BuildInput(request),
            // O-5: orden por nombre, para que el prefijo cacheado no dependa del orden en que se armaron. Sin tool_choice: auto (C-8).
            ["tools"] = new JsonArray([.. request.Tools.OrderBy(tool => tool.Name, StringComparer.Ordinal).Select(ToTool)]),
            ["max_output_tokens"] = request.MaxOutputTokens,
            // O-3: sin estado del lado del proveedor; el razonamiento viaja cifrado de una vuelta a la siguiente.
            ["store"] = false,
            ["include"] = new JsonArray("reasoning.encrypted_content"),
        };

        // O-6: el esfuerzo sólo si la lista curada lo trae; sin él, el default del modelo.
        if (request.Effort is { } effort)
        {
            body["reasoning"] = new JsonObject { ["effort"] = effort };
        }

        return body;
    }

    /// <summary>
    /// O-5 y O-4: el historial en texto, el saludo como <c>developer</c> después del primer turno del cliente, el contexto volátil como
    /// <c>developer</c> al final del historial, y después las vueltas de este trabajo, append-only: el contexto volátil queda donde estaba en
    /// la primera llamada. Dos <c>developer</c> seguidos no se mandan: con un solo turno del cliente, saludo y contexto van juntos.
    /// </summary>
    private static JsonArray BuildInput(AiModelRequest request)
    {
        var input = new JsonArray();
        var firstUser = true;
        string? pendingDeveloper = null;
        foreach (var message in request.Messages)
        {
            if (pendingDeveloper is not null)
            {
                input.Add(Message("developer", pendingDeveloper));
                pendingDeveloper = null;
            }

            if (message.Role == AiChatRole.User)
            {
                input.Add(Message("user", message.Text));
                if (firstUser && request.GreetingSent is { } greeting)
                {
                    pendingDeveloper = $"Al abrir la conversación ya le enviaste al cliente este saludo: «{greeting}». No lo repitas.";
                }

                firstUser = false;
            }
            else
            {
                input.Add(Message("assistant", message.Text));
            }
        }

        var volatileContext = string.IsNullOrWhiteSpace(request.VolatileContext) ? null : request.VolatileContext;
        var tail = string.Join("\n\n", new[] { pendingDeveloper, volatileContext }.OfType<string>());
        if (tail.Length > 0)
        {
            input.Add(Message("developer", tail));
        }

        foreach (var round in request.PriorAssistantBlocks)
        {
            foreach (var item in round.AssistantBlocks)
            {
                input.Add(JsonNode.Parse(item.Json));
            }

            // function_call_output no tiene campo de error: el fallido lleva en output el JSON de error que armó el bucle común.
            foreach (var result in round.ToolResults)
            {
                input.Add(new JsonObject
                {
                    ["type"] = "function_call_output",
                    ["call_id"] = result.CallId,
                    ["output"] = result.ContentJson,
                });
            }
        }

        return input;
    }

    private static JsonObject Message(string role, string text) => new() { ["role"] = role, ["content"] = text };

    private static JsonObject ToTool(AiToolDefinition tool) =>
        new()
        {
            ["type"] = "function",
            ["name"] = tool.Name,
            ["description"] = tool.Description,
            ["parameters"] = JsonNode.Parse(tool.InputSchemaJson)
                ?? throw new InvalidOperationException($"The schema of tool '{tool.Name}' is empty."),
            // C-8: strict con additionalProperties false y todo en required, como lo declara el esquema neutral, salvo la que se declara no
            // estricta (AiToolDefinition.Strict).
            ["strict"] = tool.Strict,
        };

    private AiModelResponse Read(string body, string model, string? requestId)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(body);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new AiInvalidResponseException("unreadable", requestId);
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new AiInvalidResponseException("unreadable", requestId);
        }

        requestId ??= StringOf(root, "id");
        var usage = UsageOf(root);
        var servedModel = StringOf(root, "model") ?? model;
        var status = StringOf(root, "status");
        LogCompleted(logger, requestId, servedModel, status, usage.Input, usage.Output);

        if (status == "failed")
        {
            var code = root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object ? StringOf(error, "code") : null;
            throw FailedOf(code, requestId, usage, servedModel);
        }

        if (status == "incomplete")
        {
            var reason = root.TryGetProperty("incomplete_details", out var details) && details.ValueKind == JsonValueKind.Object
                ? StringOf(details, "reason")
                : null;
            throw reason == "content_filter"
                ? new AiRefusedException(reason, requestId) { Usage = usage, ServedModel = servedModel }
                : new AiInvalidResponseException(reason ?? "incomplete", requestId) { Usage = usage, ServedModel = servedModel };
        }

        if (status != "completed")
        {
            throw new AiInvalidResponseException(status ?? "no_status", requestId) { Usage = usage, ServedModel = servedModel };
        }

        return ToResponse(root, usage, servedModel, requestId);
    }

    private static AiModelResponse ToResponse(JsonElement root, AiModelUsage usage, string servedModel, string? requestId)
    {
        var calls = new List<AiToolCall>();
        var opaque = new List<AiOpaqueBlock>();
        var texts = new List<string>();
        var refused = false;
        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                // O-4: el ítem completo, tal cual llegó, para reenviarlo sin tocar.
                opaque.Add(new AiOpaqueBlock(item.GetRawText()));
                switch (StringOf(item, "type"))
                {
                    case "function_call":
                        calls.Add(new AiToolCall(
                            StringOf(item, "call_id") ?? throw Invalid("call_without_id"),
                            StringOf(item, "name") ?? throw Invalid("call_without_name"),
                            StringOf(item, "arguments") ?? "{}"));
                        break;
                    case "message":
                        refused |= ReadMessage(item, texts);
                        break;
                }
            }
        }

        if (refused)
        {
            throw new AiRefusedException(null, requestId) { Usage = usage, ServedModel = servedModel };
        }

        var joined = texts.Count == 0 ? null : string.Join("\n\n", texts);
        if (calls.Count == 0)
        {
            // C-8: sin herramienta forzada, una respuesta con sólo texto es la respuesta al cliente, sin fuentes.
            if (!string.IsNullOrWhiteSpace(joined))
            {
                calls.Add(new AiToolCall(
                    $"{StringOf(root, "id")}:end_turn",
                    SendReplyTool,
                    JsonSerializer.Serialize(new { text = joined, sourceIds = Array.Empty<string>() })));
                return new AiModelResponse(joined, calls, opaque, AiStopReason.EndTurn, usage, servedModel, requestId);
            }

            throw Invalid("empty");
        }

        return new AiModelResponse(joined, calls, opaque, AiStopReason.ToolUse, usage, servedModel, requestId);

        AiInvalidResponseException Invalid(string reason) =>
            new(reason, requestId) { Usage = usage, ServedModel = servedModel };
    }

    /// <summary>Junta el texto visible del mensaje; <c>true</c> si trae una parte <c>refusal</c>.</summary>
    private static bool ReadMessage(JsonElement message, List<string> texts)
    {
        var refused = false;
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var part in content.EnumerateArray())
        {
            switch (StringOf(part, "type"))
            {
                case "output_text" when StringOf(part, "text") is { Length: > 0 } text:
                    texts.Add(text);
                    break;
                case "refusal":
                    refused = true;
                    break;
            }
        }

        return refused;
    }

    /// <summary>O-7: <c>cached_tokens</c> es lo leído de caché; OpenAI no reporta lo escrito, así que va <c>null</c> (C-4).</summary>
    private static AiModelUsage UsageOf(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return new AiModelUsage(0, 0, null, null);
        }

        long? cached = usage.TryGetProperty("input_tokens_details", out var details)
            && details.ValueKind == JsonValueKind.Object
            && details.TryGetProperty("cached_tokens", out var tokens)
            && tokens.ValueKind == JsonValueKind.Number
                ? tokens.GetInt64()
                : null;
        return new AiModelUsage(LongOf(usage, "input_tokens"), LongOf(usage, "output_tokens"), null, cached);
    }

    /// <summary>O-8: una respuesta 200 con <c>status: failed</c>, por <c>error.code</c>.</summary>
    private static AiModelException FailedOf(string? code, string? requestId, AiModelUsage usage, string servedModel) =>
        code switch
        {
            "rate_limit_exceeded" => new AiRateLimitedException(null, requestId) { Usage = usage, ServedModel = servedModel },
            "server_error" => new AiProviderUnavailableException(requestId) { Usage = usage, ServedModel = servedModel },
            _ => new AiInvalidRequestException(requestId) { Usage = usage, ServedModel = servedModel },
        };

    /// <summary>O-9: por status; el 429 además por <c>error.type</c> y <c>error.code</c>, nunca por el texto.</summary>
    private static AiModelException Translate(int status, string? errorType, string? errorCode, TimeSpan? retryAfter, string? requestId) =>
        status switch
        {
            401 => new AiCredentialsRejectedException(requestId),
            403 => new AiPermissionDeniedException(requestId),
            // Sobre /responses el único recurso es el modelo: no existe o la cuenta no tiene acceso.
            404 => new AiModelUnavailableException(requestId),
            429 when errorType == "insufficient_quota" || QuotaCodes.Contains(errorCode, StringComparer.Ordinal) =>
                new AiQuotaExhaustedException(requestId),
            429 => new AiRateLimitedException(retryAfter, requestId),
            408 or >= 500 => new AiProviderUnavailableException(requestId),
            // 400, 413, 422 y cualquier otro 4xx: la petición no sirve tal como está, reintentarla no cambia nada.
            _ => new AiInvalidRequestException(requestId),
        };

    private static (string? Type, string? Code) ErrorOf(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                    ? (StringOf(error, "type"), StringOf(error, "code"))
                    : (null, null);
        }
        catch (JsonException)
        {
            return (null, null);
        }
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

    private static string? StringOf(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long LongOf(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : 0;
}
