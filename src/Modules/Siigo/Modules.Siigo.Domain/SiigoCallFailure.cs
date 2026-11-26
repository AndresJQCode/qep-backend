using BuildingBlocks.Domain;

namespace Modules.Siigo.Domain;

/// <summary>
/// Una llamada a Siigo que falló (spec 2026-11-21): el <c>curl</c> de la petición, con el token y la <c>access_key</c> ya enmascarados,
/// y lo que Siigo contestó. Append-only: se crea y no se muta; lo único que se hace después es borrarla cuando pasa de 7 días. Una llamada
/// que responde bien no se guarda nunca (la respuesta de <c>/auth</c> trae el token). <see cref="RequestCurl"/> y
/// <see cref="ResponseBody"/> llevan datos del cliente: la lista no los devuelve y el <c>ToString</c> no los escribe.
/// </summary>
public sealed class SiigoCallFailure
{
    public const int MethodMaxLength = 10;
    public const int PathMaxLength = 500;
    public const int ReasonMaxLength = 60;
    public const int TraceIdMaxLength = 120;
    public const int OperationMaxLength = 40;
    public const int RequestCurlMaxLength = 30_000;
    public const int ResponseBodyMaxLength = 8_000;

    private SiigoCallFailure()
    {
        Method = string.Empty;
        Path = string.Empty;
        Reason = string.Empty;
        RequestCurl = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ConnectionId { get; private set; }

    public SiigoCallOperation Operation { get; private set; }

    public string Method { get; private set; }

    /// <summary>La ruta de Siigo con su query: la identificación de un cliente viaja en ella.</summary>
    public string Path { get; private set; }

    /// <summary>Null si no hubo respuesta (timeout o red).</summary>
    public int? Status { get; private set; }

    /// <summary><c>http_400</c>, <c>timeout</c>, <c>network</c>, <c>credentials_rejected</c>…</summary>
    public string Reason { get; private set; }

    /// <summary>La de la factura que se intentó crear: la une con <c>siigo.invoices</c> y con su pedido.</summary>
    public string? IdempotencyKey { get; private set; }

    /// <summary>El id de Siigo de la factura, en el sellado y sus errores.</summary>
    public string? SiigoInvoiceId { get; private set; }

    public string RequestCurl { get; private set; }

    public string? ResponseBody { get; private set; }

    public Guid? SubjectId { get; private set; }

    /// <summary>El mismo de <c>platform.request_failures</c> y del log de la aplicación.</summary>
    public string? TraceId { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public static SiigoCallFailure Create(
        Guid tenantId,
        Guid connectionId,
        SiigoCallOperation operation,
        string method,
        string path,
        int? status,
        string reason,
        string? idempotencyKey,
        string? siigoInvoiceId,
        string requestCurl,
        string? responseBody,
        Guid? subjectId,
        string? traceId,
        DateTimeOffset occurredAt) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ConnectionId = connectionId,
            Operation = operation,
            Method = Fit(method, MethodMaxLength) ?? "?",
            Path = Fit(path, PathMaxLength) ?? "/",
            Status = status,
            Reason = Fit(reason, ReasonMaxLength) ?? "unknown",
            IdempotencyKey = Fit(idempotencyKey, SiigoInvoice.KeyMaxLength),
            SiigoInvoiceId = Fit(siigoInvoiceId, SiigoInvoice.SiigoIdMaxLength),
            RequestCurl = Fit(requestCurl, RequestCurlMaxLength) ?? string.Empty,
            ResponseBody = Fit(responseBody, ResponseBodyMaxLength),
            SubjectId = subjectId,
            TraceId = Fit(traceId, TraceIdMaxLength),
            OccurredAt = occurredAt,
        };

    /// <summary>Datos del cliente: nada de la fila sale en un texto.</summary>
    public override string ToString() => "SiigoCallFailure { }";

    private static string? Fit(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null : SnapshotText.Fit(value, maxLength);
}
