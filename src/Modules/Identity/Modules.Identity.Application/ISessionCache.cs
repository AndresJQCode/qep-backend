namespace Modules.Identity.Application;

/// <summary>
/// La foto de una sesión válida que guarda la caché (spec 2026-11-13, D-4). <see cref="ValidUntil"/> es
/// <c>min(ExpiresAt, LastSeenAt + IdleTimeout)</c>: pasado ese instante la sesión ya no vale aunque la entrada siga viva.
/// </summary>
public sealed record CachedSession(Guid SessionId, Guid UserId, DateTimeOffset LastSeenAt, DateTimeOffset ValidUntil);

/// <summary>Una sesión revocada o borrada: el id va al log de una falla, el hash es la clave de su entrada.</summary>
public sealed record RevokedSession(Guid SessionId, string TokenHash);

/// <summary>
/// Caché de la validación de sesiones (spec 2026-11-13, D-4). La clave es el hash del token que ya guarda la fila, nunca el
/// token crudo. Ninguna operación lanza por un fallo de la caché: la lectura devuelve <c>null</c> y se lee Postgres, así que
/// un fallo nunca da por válida una sesión. Quien revoca llama <see cref="RemoveAsync"/> antes del guardado y otra vez
/// después.
/// </summary>
public interface ISessionCache
{
    /// <returns>La entrada, o <c>null</c> si no está, si la sesión se marcó revocada o si la caché no respondió.</returns>
    Task<CachedSession?> GetAsync(string tokenHash, CancellationToken cancellationToken);

    /// <summary>Guarda la foto con TTL <c>min(ValidUntil - ahora, 15 min)</c>. Nunca lanza.</summary>
    Task SetAsync(string tokenHash, CachedSession session, CancellationToken cancellationToken);

    /// <summary>
    /// Marca cada sesión como revocada y borra su entrada. La marca gana sobre una entrada que escriba después un request que
    /// leyó Postgres antes de la revocación. Nunca lanza; si falla, lo loguea en <c>Error</c> con el id de cada sesión.
    /// </summary>
    Task RemoveAsync(IReadOnlyCollection<RevokedSession> sessions, CancellationToken cancellationToken);
}
