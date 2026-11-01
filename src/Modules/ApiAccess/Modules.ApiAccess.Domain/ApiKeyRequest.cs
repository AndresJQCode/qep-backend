namespace Modules.ApiAccess.Domain;

/// <summary>
/// Una llamada autenticada a la API externa, para que el tenant la depure. Sólo se escribe: nunca
/// guarda la clave, el cuerpo, la query string, los headers, el teléfono, los parámetros ni la
/// <c>idempotencyKey</c>.
/// </summary>
public sealed class ApiKeyRequest
{
    public const int RetentionDays = 15;
    public const int PathMaxLength = 512;
    public const int ErrorCodeMaxLength = 100;
    public const int TraceIdMaxLength = 128;
    public const int MethodMaxLength = 10;

    // Para EF Core.
    private ApiKeyRequest()
    {
        ApiKeyName = string.Empty;
        Method = string.Empty;
        Path = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ApiKeyId { get; private set; }

    public string ApiKeyName { get; private set; }

    public string Method { get; private set; }

    public string Path { get; private set; }

    public int StatusCode { get; private set; }

    public string? ErrorCode { get; private set; }

    public Guid? MessageId { get; private set; }

    public string? TraceId { get; private set; }

    public int DurationMs { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public static ApiKeyRequest Record(
        Guid tenantId,
        Guid apiKeyId,
        string apiKeyName,
        string method,
        string path,
        int statusCode,
        string? errorCode,
        Guid? messageId,
        string? traceId,
        int durationMs,
        DateTimeOffset occurredAt) =>
        new()
        {
            // V7 por orden de tiempo: las filas nuevas caen al final del índice de la clave.
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ApiKeyId = apiKeyId,
            ApiKeyName = Cut(apiKeyName, ApiKey.NameMaxLength),
            Method = Cut(method, MethodMaxLength),
            Path = Cut(path, PathMaxLength),
            StatusCode = statusCode,
            // Un éxito no tiene código de error, venga lo que venga en el contexto.
            ErrorCode = statusCode < 400 ? null : CutOrNull(errorCode, ErrorCodeMaxLength),
            MessageId = messageId,
            TraceId = CutOrNull(traceId, TraceIdMaxLength),
            DurationMs = Math.Max(0, durationMs),
            OccurredAt = occurredAt
        };

    // Una fila que no cabe en su columna haría fallar el INSERT y se perdería el registro entero.
    private static string Cut(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private static string? CutOrNull(string? value, int max) =>
        string.IsNullOrEmpty(value) ? null : Cut(value, max);
}
