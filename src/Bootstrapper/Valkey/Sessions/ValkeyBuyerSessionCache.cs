using System.Text.Json;
using Microsoft.Extensions.Logging;
using Modules.Buyers.Application;
using Modules.Buyers.Domain;
using StackExchange.Redis;

namespace Bootstrapper.Valkey.Sessions;

/// <summary>
/// La caché de sesión del comprador con <c>Valkey:Areas:Sessions</c> prendido (spec 2026-11-13, D-4). Mismo patrón que
/// <see cref="ValkeySessionCache"/>: entrada <c>qep:sess:buyer:{hash}</c>, marca de revocación <c>qep:sess:buyer:rev:{hash}</c>
/// que gana sobre una entrada escrita después (plan P2), un solo <c>MGET</c> para leer las dos, y todo por
/// <see cref="ValkeyGateway.TryAsync{T}"/>. El TTL es <c>min(lo que le queda, 15 min)</c>, con lo que le queda =
/// <c>min(ExpiresAt, LastSeenAt + IdleTimeout)</c>.
/// </summary>
/// <remarks>
/// Dos relojes: <c>LastSeenAt</c> y <c>ValidUntil</c> (o <c>ExpiresAt</c>) los escribe el módulo con su <c>IClock</c>, y el TTL se
/// calcula acá con <see cref="TimeProvider"/>. En producción los dos son la hora del sistema; en una prueba que mueve sólo uno, el
/// TTL puede salir distinto del que la prueba espera, pero nunca pasa de <see cref="SessionCacheTtl.MaxEntry"/>.
/// </remarks>
internal sealed partial class ValkeyBuyerSessionCache(
    ValkeyGateway gateway,
    TimeProvider time,
    ILogger<ValkeyBuyerSessionCache> logger)
    : IBuyerSessionCache
{
    private const string EntryPrefix = ValkeyKeys.Prefix + "sess:buyer:";
    private const string MarkerPrefix = ValkeyKeys.Prefix + "sess:buyer:rev:";

    public async Task<CachedBuyerSession?> GetAsync(string tokenHash, CancellationToken cancellationToken)
    {
        var attempt = await gateway.TryAsync(
            ValkeyArea.Sessions,
            database => database.StringGetAsync([EntryKey(tokenHash), MarkerKey(tokenHash)]),
            cancellationToken).ConfigureAwait(false);
        if (attempt is not { Succeeded: true, Value: [var entry, var marker] } || !marker.IsNull || entry.IsNull)
        {
            return null;
        }

        return SessionCacheJson.TryRead<CachedBuyerSession>(entry);
    }

    public async Task SetAsync(string tokenHash, CachedBuyerSession session, CancellationToken cancellationToken)
    {
        var idleLimit = session.LastSeenAt + BuyerSession.IdleTimeout;
        var validUntil = session.ExpiresAt < idleLimit ? session.ExpiresAt : idleLimit;
        if (SessionCacheTtl.EntryTtl(validUntil, time.GetUtcNow()) is not { } ttl)
        {
            return;
        }

        var value = JsonSerializer.Serialize(session, SessionCacheJson.Options);
        await gateway.TryAsync(
            ValkeyArea.Sessions,
            database => database.StringSetAsync(EntryKey(tokenHash), value, ttl),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(IReadOnlyCollection<RevokedBuyerSession> sessions, CancellationToken cancellationToken)
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

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Buyer session cache could not drop revoked session {SessionId}; it is served from the cache until its entry expires (at most 15 minutes).")]
    private static partial void LogRemoveFailed(ILogger logger, Guid sessionId);
}
