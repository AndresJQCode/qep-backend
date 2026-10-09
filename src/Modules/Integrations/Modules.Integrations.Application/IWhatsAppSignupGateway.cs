namespace Modules.Integrations.Application;

/// <summary>Un error de Graph ya resumido (spec §3): lo que se puede registrar. Nunca el mensaje.</summary>
public sealed record GraphFailure(int HttpStatus, int? Code, int? Subcode, string Reason);

/// <summary>Resultado de una llamada a Graph que <b>no lanza</b> por un error del proveedor: el handler
/// decide el código de dominio (P3 del plan).</summary>
public sealed record GraphResult<T>(T? Value, GraphFailure? Failure)
{
    public bool Succeeded => Failure is null;
}

public sealed record WabaPhoneNumber(string Id, string? DisplayPhoneNumber, string? VerifiedName, string? QualityRating);

/// <summary>
/// Spec 2026-10-09 §8.1, pasos 4 a 8, contra Graph. El adaptador vive en Infrastructure sobre
/// <c>integrations.meta-graph</c>. Nunca registra el <c>code</c>, el token ni el PIN.
/// </summary>
public interface IWhatsAppSignupGateway
{
    /// <summary>GET /{v}/oauth/access_token?client_id&amp;client_secret&amp;code → token de sistema del negocio.</summary>
    Task<GraphResult<string>> ExchangeCodeAsync(string code, CancellationToken cancellationToken);

    /// <summary>POST /{phoneNumberId}/register con { messaging_product, pin }.</summary>
    Task<GraphResult<bool>> RegisterNumberAsync(string phoneNumberId, string accessToken, string pin, CancellationToken cancellationToken);

    /// <summary>GET /{wabaId}/phone_numbers.</summary>
    Task<GraphResult<IReadOnlyList<WabaPhoneNumber>>> ListPhoneNumbersAsync(string wabaId, string accessToken, CancellationToken cancellationToken);

    /// <summary>POST /{wabaId}/subscribed_apps.</summary>
    Task<GraphResult<bool>> SubscribeAppAsync(string wabaId, string accessToken, CancellationToken cancellationToken);

    /// <summary>GET /{phoneNumberId}?fields=display_phone_number,verified_name,quality_rating,status.</summary>
    Task<GraphResult<WabaPhoneNumber>> GetPhoneNumberAsync(string phoneNumberId, string accessToken, CancellationToken cancellationToken);
}
