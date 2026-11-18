using Microsoft.Extensions.Caching.Memory;
using Modules.Identity.Application;

namespace Bootstrapper.Valkey.Sessions;

/// <summary>
/// La caché de sesión de Identity del pod, la que rige con <c>Valkey:Areas:Sessions</c> apagado. Antes el área apagada leía
/// <c>identity.sessions</c> en cada request autenticado; con la base en otro nodo ese viaje se paga antes de todo lo demás. Mismos
/// TTL que <see cref="ValkeySessionCache"/> (<see cref="SessionCacheTtl"/>) y la misma marca de revocación: <see cref="RemoveAsync"/>
/// borra la entrada y deja la marca, así una entrada que escriba después un request que leyó Postgres antes del commit no se
/// sirve. <c>SessionService</c> sigue decidiendo cuándo tocar <c>last_seen_at</c>: una entrada de más de cinco minutos es un fallo.
/// </summary>
/// <remarks>
/// Con más de una réplica, una revocación en un pod no saca la entrada del otro hasta que venza (≤ 15 min): el área de Valkey es
/// prerrequisito de <c>replicas &gt; 1</c> (<c>MultiReplicaInventory.SharedStateSwitches</c>).
/// </remarks>
internal sealed class MemorySessionCache : ISessionCache, IDisposable
{
    private readonly TimeProvider _time;
    private readonly MemoryCache _entries;

    public MemorySessionCache(TimeProvider time)
        : this(time, 50_000)
    {
    }

    /// <param name="time">El reloj.</param>
    /// <param name="sizeLimit">Tope de entradas (una unidad cada una). Las pruebas lo bajan para forzar la compactación.</param>
    internal MemorySessionCache(TimeProvider time, long sizeLimit)
    {
        _time = time;
        _entries = new MemoryCache(new MemoryCacheOptions { SizeLimit = sizeLimit });
    }

    /// <summary>Cuántas entradas hay, marcas incluidas. Sólo para las pruebas.</summary>
    internal int Count => _entries.Count;

    public Task<CachedSession?> GetAsync(string tokenHash, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        if (IsMarkedRevoked(tokenHash, now))
        {
            return Task.FromResult<CachedSession?>(null);
        }

        if (!_entries.TryGetValue(EntryKey(tokenHash), out Entry? entry) || entry is null || now >= entry.ExpiresAt)
        {
            return Task.FromResult<CachedSession?>(null);
        }

        return Task.FromResult<CachedSession?>(entry.Session);
    }

    public Task SetAsync(string tokenHash, CachedSession session, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        // Sin tiempo restante no se escribe; revocada hace poco, la marca gana sobre la escritura tardía.
        if (SessionCacheTtl.EntryTtl(session.ValidUntil, now) is not { } ttl || IsMarkedRevoked(tokenHash, now))
        {
            return Task.CompletedTask;
        }

        _entries.Set(
            EntryKey(tokenHash),
            new Entry(session, now + ttl),
            new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = ttl });
        return Task.CompletedTask;
    }

    public Task RemoveAsync(IReadOnlyCollection<RevokedSession> sessions, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        foreach (var session in sessions)
        {
            // NeverRemove: la marca comparte el tope de tamaño con las entradas, y la compactación saca primero lo menos usado,
            // que es justo la marca. Sin esto podría irse antes que la entrada vieja que vigila. Igual vence por su TTL.
            _entries.Set(
                MarkerKey(session.TokenHash),
                new Stamp(now + SessionCacheTtl.RevocationMarker),
                new MemoryCacheEntryOptions
                {
                    Size = 1,
                    AbsoluteExpirationRelativeToNow = SessionCacheTtl.RevocationMarker,
                    Priority = CacheItemPriority.NeverRemove,
                });
            _entries.Remove(EntryKey(session.TokenHash));
        }

        return Task.CompletedTask;
    }

    public void Dispose() => _entries.Dispose();

    private bool IsMarkedRevoked(string tokenHash, DateTimeOffset now) =>
        _entries.TryGetValue(MarkerKey(tokenHash), out Stamp? marker) && marker is not null && now < marker.ExpiresAt;

    private static string EntryKey(string tokenHash) => "sess:" + tokenHash;

    private static string MarkerKey(string tokenHash) => "sess:rev:" + tokenHash;

    private sealed record Entry(CachedSession Session, DateTimeOffset ExpiresAt);

    private sealed record Stamp(DateTimeOffset ExpiresAt);
}
