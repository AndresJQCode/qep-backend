using Modules.Automations.Domain.Engine;
using Modules.Automations.Domain.Flow;

namespace Modules.Automations.Application.Http;

/// <summary>Quién llama al bloque: una corrida (<see cref="RunId"/>) o una simulación con «llamar a la API de verdad»
/// (<see cref="SimulationId"/>, D-S2).</summary>
public sealed record HttpCallContext(Guid TenantId, Guid? RunId, Guid? SimulationId, string NodeId);

/// <summary>
/// Cómo terminó el bloque (D-H4..D-H8). <see cref="Ok"/> elige la salida <c>ok</c> o <c>error</c>; <see cref="Mapped"/> trae sólo las
/// variables del mapeo (D-H7: sin mapeo, vacío); <see cref="Outcome"/> es uno de <see cref="HttpCallOutcomes"/> y <see cref="ErrorCode"/> lo
/// precisa (<c>http_404</c>, <c>body_not_json</c>, el motivo de la política de URL), <c>null</c> si salió bien.
/// </summary>
public sealed record HttpCallResult(bool Ok, int? Status, RunVariables Mapped, string Outcome, string? ErrorCode);

/// <summary>Spec 2026-10-21 D-H4..D-H8: llama a la API externa de una conexión <c>external-api</c> y mapea su respuesta. Nunca lanza por la
/// API (todo fallo sale como un <see cref="HttpCallResult"/> sin <c>Ok</c>); sólo propaga la cancelación de quien llama.</summary>
public interface IHttpBlockExecutor
{
    Task<HttpCallResult> ExecuteAsync(HttpCallContext context, HttpNode node, RunVariables variables, CancellationToken cancellationToken);
}

/// <summary>D-H6: una fila de <c>automations.http_calls</c>. La ruta va interpolada y sin query; nunca cuerpo, headers ni clave.</summary>
public sealed record HttpCallEntry(
    Guid TenantId, Guid? RunId, Guid? SimulationId, string NodeId, Guid ConnectionId, string Method, string Path, int? StatusCode, int DurationMs,
    string Outcome, string? ErrorCode, DateTimeOffset CalledAt);

/// <summary>D-H6: el registro de llamadas. Se confirma por su cuenta, fuera de la transacción de la unidad del motor: la llamada ya salió
/// aunque la unidad se deshaga.</summary>
public interface IHttpCallLog
{
    Task RecordAsync(HttpCallEntry entry, CancellationToken cancellationToken);
}

/// <summary>Los <c>outcome</c> de <c>http_calls</c>. D-H6 nombra los cinco primeros; el resto precisa el <c>error</c> del bloque.
/// <see cref="ConnectionUnavailable"/> nunca se registra: sin conexión no hay fila que escribir.</summary>
public static class HttpCallOutcomes
{
    public const string Ok = "ok";
    public const string ErrorStatus = "error_status";
    public const string Timeout = "timeout";
    public const string Network = "network";
    public const string MappingFailed = "mapping_failed";
    public const string UrlRejected = "url_rejected";
    public const string ConnectionUnavailable = "connection_unavailable";
    public const string MediaNotReady = "media_not_ready";
    public const string ResponseTooLarge = "response_too_large";
    public const string BodyNotJson = "body_not_json";
}
