using System.Text.Json;
using Microsoft.Extensions.Logging;
using Modules.Identity.Application;
using StackExchange.Redis;

namespace Bootstrapper.Valkey.Sessions;

/// <summary>Los TTL de la caché de sesión (spec 2026-11-13, D-4; plan P2), compartidos por Identity y Buyers.</summary>
internal static class SessionCacheTtl
{
    /// <summary>Lo más que vive una entrada: acota cuánto puede servirse una sesión si todo lo demás falla.</summary>
    public static readonly TimeSpan MaxEntry = TimeSpan.FromMinutes(15);

    /// <summary>La marca de revocación dura más que la entrada más larga: cubre el desfase de un lector atrasado.</summary>
    public static readonly TimeSpan RevocationMarker = TimeSpan.FromMinutes(20);

    /// <summary><c>min(validUntil - now, MaxEntry)</c>; <c>null</c> si ya no queda tiempo y no hay que escribir.</summary>
    public static TimeSpan? EntryTtl(DateTimeOffset validUntil, DateTimeOffset now)
    {
        var remaining = validUntil - now;
        if (remaining <= TimeSpan.Zero)
        {
            return null;
        }

        return remaining < MaxEntry ? remaining : MaxEntry;
    }
}

/// <summary>
/// La caché de sesión de Identity con <c>Valkey:Areas:Sessions</c> prendido (spec 2026-11-13, D-4). La entrada es
/// <c>qep:sess:{hash}</c> (JSON de <see cref="CachedSession"/>) y revocar escribe además la marca <c>qep:sess:rev:{hash}</c>
/// (plan P2): <see cref="GetAsync"/> lee las dos en un solo <c>MGET</c> y, con marca, es un fallo de caché. Así una entrada que
/// escribe después del segundo <c>DEL</c> un request que leyó Postgres antes del commit nunca se sirve. Todo va por
/// <see cref="ValkeyGateway.TryAsync{T}"/>; sin éxito, la lectura es un fallo de caché y se lee Postgres.
/// </summary>
/// <remarks>
/// Dos relojes: <c>LastSeenAt</c> y <c>ValidUntil</c> (o <c>ExpiresAt</c>) los escribe el módulo con su <c>IClock</c>, y el TTL se
/// calcula acá con <see cref="TimeProvider"/>. En producción los dos son la hora del sistema; en una prueba que mueve sólo uno, el
/// TTL puede salir distinto del que la prueba espera, pero nunca pasa de <see cref="SessionCacheTtl.MaxEntry"/>.
/// </remarks>
internal sealed partial class ValkeySessionCache(
    ValkeyGateway gateway,
    TimeProvider time,
    ILogger<ValkeySessionCache> logger)
    : ISessionCache
{
    private const string EntryPrefix = ValkeyKeys.Prefix + "sess:";
    private const string MarkerPrefix = ValkeyKeys.Prefix + "sess:rev:";

    public async Task<CachedSession?> GetAsync(string tokenHash, CancellationToken cancellationToken)
    {
        var attempt = await gateway.TryAsync(
            ValkeyArea.Sessions,
            database => database.StringGetAsync([EntryKey(tokenHash), MarkerKey(tokenHash)]),
            cancellationToken).ConfigureAwait(false);
        if (attempt is not { Succeeded: true, Value: [var entry, var marker] } || !marker.IsNull || entry.IsNull)
        {
            return null;
        }

        return SessionCacheJson.TryRead<CachedSession>(entry);
    }

    public async Task SetAsync(string tokenHash, CachedSession session, CancellationToken cancellationToken)
    {
        if (SessionCacheTtl.EntryTtl(session.ValidUntil, time.GetUtcNow()) is not { } ttl)
        {
            return;
        }

        var value = JsonSerializer.Serialize(session, SessionCacheJson.Options);
        await gateway.TryAsync(
            ValkeyArea.Sessions,
            database => database.StringSetAsync(EntryKey(tokenHash), value, ttl),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(IReadOnlyCollection<RevokedSession> sessions, CancellationToken cancellationToken)
    {
        if (sessions.Count == 0)
        {
            return;
        }

        // Sin el token de quien llama: el segundo RemoveAsync corre después del commit, y un request cancelado ahí no puede
        // saltárselo. El tope lo pone OperationTimeoutMs del gateway.
        var attempt = await gateway.TryAsync(
            ValkeyArea.Sessions,
            database => SessionCacheRevocation.MarkAndDeleteAsync(
                database, sessions.Select(session => (MarkerKey(session.TokenHash), EntryKey(session.TokenHash)))),
            CancellationToken.None).ConfigureAwait(false);
        if (!attempt.Succeeded)
        {
            foreach (var session in sessions)
            {
                LogRemoveFailed(logger, session.SessionId);
            }
        }
    }

    private static RedisKey EntryKey(string tokenHash) => EntryPrefix + tokenHash;

    private static RedisKey MarkerKey(string tokenHash) => MarkerPrefix + tokenHash;

    // Sin el hash ni el token: el id basta para encontrar la fila (D-4, «un fallo se loguea en Error con el id»).
    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Session cache could not drop revoked session {SessionId}; it is served from the cache until its entry expires (at most 15 minutes).")]
    private static partial void LogRemoveFailed(ILogger logger, Guid sessionId);
}

/// <summary>Lo que comparten las dos cachés de sesión: la escritura de la marca con el borrado y la lectura del JSON.</summary>
internal static class SessionCacheRevocation
{
    /// <summary>Por sesión, <c>SET marca 1 EX 1200</c> y <c>DEL entrada</c>, todo en un batch; claves explícitas, nunca patrón.</summary>
    public static async Task<bool> MarkAndDeleteAsync(IDatabase database, IEnumerable<(RedisKey Marker, RedisKey Entry)> keys)
    {
        var batch = database.CreateBatch();
        var pending = new List<Task>();
        foreach (var (marker, entry) in keys)
        {
            pending.Add(batch.StringSetAsync(marker, 1, SessionCacheTtl.RevocationMarker));
            pending.Add(batch.KeyDeleteAsync(entry));
        }

        batch.Execute();
        await Task.WhenAll(pending).ConfigureAwait(false);
        return true;
    }
}

internal static class SessionCacheJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Una entrada que no se puede leer (formato viejo, corrupta) es un fallo de caché, no un error.</summary>
    public static T? TryRead<T>(RedisValue value)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>((string)value!, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
