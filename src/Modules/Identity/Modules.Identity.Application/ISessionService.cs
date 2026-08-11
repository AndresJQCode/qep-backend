namespace Modules.Identity.Application;

public sealed record SessionIssueResult(string RawToken, DateTimeOffset ExpiresAt);

public sealed record SessionPrincipal(Guid UserId);

/// <summary>
/// Emite, valida y revoca sesiones de token opaco respaldadas por la tabla
/// <c>identity.sessions</c>. El token crudo se devuelve al llamador exactamente una vez,
/// al emitirlo, y nunca se persiste — sólo se guarda su hash, así que una lectura de la
/// base no se puede reproducir como sesión válida (ver <c>Session</c>).
/// </summary>
public interface ISessionService
{
    Task<SessionIssueResult> IssueAsync(
        Guid userId,
        string? userAgent,
        string? ipAddress,
        CancellationToken cancellationToken);

    /// <returns>El principal de la sesión, o <c>null</c> si el token es desconocido,
    /// venció, expiró por inactividad o fue revocado.</returns>
    Task<SessionPrincipal?> ValidateAsync(string rawToken, CancellationToken cancellationToken);

    Task RevokeAsync(string rawToken, string reason, CancellationToken cancellationToken);

    /// <returns>La cantidad de sesiones revocadas.</returns>
    Task<int> RevokeAllForUserAsync(
        Guid userId,
        string reason,
        CancellationToken cancellationToken);
}
