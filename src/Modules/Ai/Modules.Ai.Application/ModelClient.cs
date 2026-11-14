using Modules.Ai.Domain;

namespace Modules.Ai.Application;

/// <summary>
/// Plan 2026-11-12, 5.2: el modelo de cualquier proveedor, con tipos propios. Application sólo conoce este puerto (ningún SDK sale de
/// Infrastructure, AiLayerTests); cada adaptador traduce la petición a su API y sus errores a los tipados de <see cref="AiModelException"/>.
/// El adaptador no reintenta: los reintentos son del worker, iguales para los dos proveedores (C-13, D-A17).
/// </summary>
public interface IAiModelClient
{
    Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken);
}

/// <summary>El adaptador del proveedor de <b>la versión de la sesión</b> (C-17, C-20): nunca el del borrador ni el del tenant, y nunca
/// otro proveedor como respaldo.</summary>
public interface IAiModelClientResolver
{
    // El nombre es el del plan (2026-11-12, 5.2). Nadie implementa este puerto desde VB, que es a quien protege CA1716.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Name fixed by the plan; C#-only port.")]
    IAiModelClient For(AiProvider provider);
}

/// <summary>
/// Una llamada al modelo. Sin estado del lado del proveedor (C-6): QEP arma todo el contexto en cada llamada.
/// </summary>
/// <param name="Credential">La clave de la cuenta del tenant en el proveedor (BYOK, C-14): un cliente por llamada con esta clave, nunca
/// uno global. Su <c>ToString</c> no la imprime, así que tampoco el de la petición.</param>
/// <param name="Effort">El esfuerzo de la lista curada (C-7); <c>null</c> si el proveedor no lo usa.</param>
/// <param name="SystemPrompt">Lo estable: instrucciones de QEP y del agente, idénticas byte a byte en la misma versión (C-12).</param>
/// <param name="GreetingSent">El saludo que ya salió al cliente, como dato aparte (C-11): cada adaptador decide dónde ponerlo.</param>
/// <param name="VolatileContext">Lo que cambia en cada llamada (fecha, ventana): va después del último punto de caché (C-12).</param>
/// <param name="Messages">El historial de la conversación, alternado y terminado en <see cref="AiChatRole.User"/>.</param>
/// <param name="PriorAssistantBlocks">Las vueltas anteriores de este mismo trabajo, en orden: lo que respondió el modelo, opaco y
/// completo, y los resultados de sus herramientas (C-10, append-only). Vacío en la primera llamada.</param>
public sealed record AiModelRequest(
    AiCredential Credential,
    string Model,
    string? Effort,
    string SystemPrompt,
    string? GreetingSent,
    string VolatileContext,
    IReadOnlyList<AiChatMessage> Messages,
    IReadOnlyList<AiModelRound> PriorAssistantBlocks,
    IReadOnlyList<AiToolDefinition> Tools,
    int MaxOutputTokens);

public enum AiChatRole
{
    User,
    Assistant,
}

/// <summary>Un turno del historial en texto: el del cliente (<see cref="AiChatRole.User"/>) o una respuesta ya enviada del agente.</summary>
public sealed record AiChatMessage(AiChatRole Role, string Text);

/// <summary>
/// Una vuelta del bucle de herramientas: lo que el modelo devolvió, tal cual (<see cref="AssistantBlocks"/>), y los resultados de sus
/// llamadas. El adaptador reenvía los bloques sin tocarlos y todos los resultados en un solo mensaje (C-10).
/// </summary>
public sealed record AiModelRound(IReadOnlyList<AiOpaqueBlock> AssistantBlocks, IReadOnlyList<AiToolResult> ToolResults);

/// <summary>Un bloque de la respuesta del proveedor en su propio formato (JSON). Sólo el adaptador que lo produjo lo entiende; el bucle común
/// lo guarda y lo devuelve sin mirarlo.</summary>
public sealed record AiOpaqueBlock(string Json);

/// <param name="ContentJson">El resultado como JSON; con <paramref name="IsError"/>, el error que lee el modelo.</param>
public sealed record AiToolResult(string CallId, string ContentJson, bool IsError);

/// <summary>Una herramienta neutral: <paramref name="InputSchemaJson"/> es JSON Schema con <c>additionalProperties: false</c> y todos los
/// campos en <c>required</c>; cada adaptador lo traduce a su API.</summary>
public sealed record AiToolDefinition(string Name, string Description, string InputSchemaJson);

/// <param name="ArgumentsJson">Los argumentos tal como los mandó el modelo, como objeto JSON.</param>
public sealed record AiToolCall(string Id, string Name, string ArgumentsJson);

public enum AiStopReason
{
    /// <summary>El modelo terminó su turno. Con texto y sin herramienta final, el adaptador ya lo convirtió en <c>send_reply</c> (C-8).</summary>
    EndTurn,

    /// <summary>El modelo pidió herramientas.</summary>
    ToolUse,
}

/// <param name="CacheCreation">Tokens escritos en caché; <c>null</c> si el proveedor no lo reporta (C-4: no es <c>0</c>).</param>
/// <param name="CacheRead">Tokens leídos de caché; <c>null</c> si el proveedor no lo reporta.</param>
public sealed record AiModelUsage(long Input, long Output, long? CacheCreation, long? CacheRead);

/// <param name="Text">El texto visible de la respuesta, si lo hubo.</param>
/// <param name="ServedModel">El modelo que de verdad respondió: con el respaldo por rechazo (C-9) puede no ser el pedido.</param>
/// <param name="RequestId">El id de la petición en el proveedor, para el log; nunca el cuerpo.</param>
public sealed record AiModelResponse(
    string? Text,
    IReadOnlyList<AiToolCall> ToolCalls,
    IReadOnlyList<AiOpaqueBlock> OpaqueAssistantBlocks,
    AiStopReason StopReason,
    AiModelUsage Usage,
    string ServedModel,
    string? RequestId);

/// <summary>
/// Plan 2026-11-12, «Errores del proveedor»: los errores tipados, comunes a los dos proveedores. Cada adaptador traduce los de su API
/// por el tipo del error, nunca por el texto. Con una respuesta 200 que no sirve (<see cref="AiRefusedException"/>,
/// <see cref="AiInvalidResponseException"/>) los tokens ya se gastaron: viajan en <see cref="Usage"/> para que el consumo se registre igual.
/// </summary>
public abstract class AiModelException(string code, string message, string? requestId, Exception? innerException)
    : Exception(message, innerException)
{
    /// <summary>El código del traspaso (<c>ai.provider.*</c>, <c>ai.agent.request_invalid</c>): contrato con el frontend.</summary>
    public string Code { get; } = code;

    public string? RequestId { get; } = requestId;

    /// <summary>Si el worker lo reintenta (D-A17). Los demás terminan en traspaso inmediato.</summary>
    public virtual bool IsRetryable => false;

    public AiModelUsage? Usage { get; init; }

    public string? ServedModel { get; init; }
}

/// <summary>Timeout, red, 5xx, sobrecarga: se reintenta; agotado → <c>ProviderFailed</c>.</summary>
public sealed class AiProviderUnavailableException(string? requestId = null, Exception? innerException = null)
    : AiModelException(AiModelErrorCodes.ProviderUnavailable, "The model provider is unavailable.", requestId, innerException)
{
    public override bool IsRetryable => true;
}

/// <summary>Límite de tasa, nunca de cuota: se reintenta respetando <see cref="RetryAfter"/> si llegó.</summary>
public sealed class AiRateLimitedException(TimeSpan? retryAfter, string? requestId = null, Exception? innerException = null)
    : AiModelException(AiModelErrorCodes.RateLimited, "The model provider rate-limited the request.", requestId, innerException)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;

    public override bool IsRetryable => true;
}

/// <summary>Sin saldo: sin reintento, y el proveedor queda con su <c>issue</c> bloqueante (C-19).</summary>
public sealed class AiQuotaExhaustedException(string? requestId = null, Exception? innerException = null)
    : AiModelException(AiModelErrorCodes.QuotaExhausted, "The provider account has no credit left.", requestId, innerException);

/// <summary>Clave rechazada: sin reintento; la conexión pasa a <c>NeedsAttention</c>.</summary>
public sealed class AiCredentialsRejectedException(string? requestId = null, Exception? innerException = null)
    : AiModelException(AiModelErrorCodes.CredentialsRejected, "The provider rejected the API key.", requestId, innerException);

/// <summary>La clave no tiene permiso: sin reintento; <c>issue</c> bloqueante del proveedor.</summary>
public sealed class AiPermissionDeniedException(string? requestId = null, Exception? innerException = null)
    : AiModelException(AiModelErrorCodes.PermissionDenied, "The API key is not allowed to use the model.", requestId, innerException);

/// <summary>El modelo no existe o la cuenta no tiene acceso: <c>AgentFailed</c>, con el <c>issue</c> en el agente.</summary>
public sealed class AiModelUnavailableException(string? requestId = null, Exception? innerException = null)
    : AiModelException(AiModelErrorCodes.ModelUnavailable, "The model is not available for this account.", requestId, innerException);

/// <summary>El proveedor rechazó la petición por su forma: <c>AgentFailed</c>; se loguea el id, nunca el cuerpo.</summary>
public sealed class AiInvalidRequestException(string? requestId = null, Exception? innerException = null)
    : AiModelException(AiModelErrorCodes.RequestInvalid, "The model provider rejected the request.", requestId, innerException);

/// <summary>Un clasificador del proveedor rechazó responder (ya pasado por el respaldo de C-9): <c>AgentFailed</c>.</summary>
public sealed class AiRefusedException(string? category, string? requestId = null)
    : AiModelException(AiModelErrorCodes.Refused, "The model refused to answer.", requestId, null)
{
    /// <summary>La categoría del proveedor, sólo para el evento interno.</summary>
    public string? Category { get; } = category;
}

/// <summary>Una respuesta que no sigue el contrato (cortada por tope de salida, sin herramienta final ni texto): un reintento, y cuenta para
/// <c>maxConsecutiveFailures</c>.</summary>
public sealed class AiInvalidResponseException(string reason, string? requestId = null)
    : AiModelException(AiModelErrorCodes.InvalidResponse, $"The model response does not follow the contract ({reason}).", requestId, null)
{
    public string Reason { get; } = reason;
}

/// <summary>Los topes de un trabajo (D-A20) y del contexto (§6.3), de <c>Ai:Model</c>. Son propuesta (P-2).</summary>
/// <param name="MaxContextTokens"><c>null</c>: sin tope por tokens, sólo por turnos.</param>
public sealed record AiRunLimits(
    int MaxOutputTokens, TimeSpan JobTimeout, int MaxModelCalls, int MaxToolCalls, int MaxHistoryTurns, int? MaxContextTokens);

/// <summary>Los códigos de la tabla de errores del plan 2026-11-12. Son contrato (eventos de traspaso, pantalla); no se renombran.</summary>
public static class AiModelErrorCodes
{
    public const string ProviderUnavailable = "ai.provider.unavailable";
    public const string RateLimited = "ai.provider.rate_limited";
    public const string QuotaExhausted = "ai.provider.quota_exhausted";
    public const string CredentialsRejected = "ai.provider.credentials_rejected";
    public const string PermissionDenied = "ai.provider.permission_denied";
    public const string ModelUnavailable = "ai.provider.model_unavailable";
    public const string RequestInvalid = "ai.agent.request_invalid";
    public const string Refused = "ai.provider.refused";

    /// <summary>La tabla no le da código propio a la respuesta inválida (cuenta para <c>maxConsecutiveFailures</c>); éste es el que usa
    /// el registro interno de la llamada.</summary>
    public const string InvalidResponse = "invalid_response";
}
