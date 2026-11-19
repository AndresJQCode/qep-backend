using System.Collections.Concurrent;

namespace Modules.Siigo.Infrastructure.Api;

/// <summary>
/// Spec 2026-11-18 §7: el token de Siigo por conexión, en memoria, hasta <c>expires_in</c> menos un margen de
/// <see cref="Margin"/>. Con varias réplicas cada pod tiene el suyo (un token de más por réplica no tiene efecto: Siigo acepta varios
/// vivos). Singleton: se clasifica en <c>MultiReplicaInventory</c>. El token nunca sale de aquí a un log, una auditoría o una
/// respuesta. El reloj es el <see cref="TimeProvider"/> (no <c>IClock</c>, que es scoped y un singleton no puede consumirlo), para probar el vencimiento.
/// </summary>
internal sealed class SiigoTokenCache(TimeProvider clock)
{
    /// <summary>Se renueva un poco antes de que venza: una llamada larga no debería arrancar con un token a segundos de caducar.</summary>
    public static readonly TimeSpan Margin = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<Guid, Entry> _tokens = new();

    /// <summary>El token vigente de la conexión, o <c>null</c> si no hay o ya entró en el margen.</summary>
    public string? TryGet(Guid connectionId) =>
        _tokens.TryGetValue(connectionId, out var entry) && entry.UsableUntil > clock.GetUtcNow() ? entry.Token : null;

    public void Set(Guid connectionId, string token, TimeSpan lifetime) =>
        _tokens[connectionId] = new Entry(token, clock.GetUtcNow() + lifetime - Margin);

    /// <summary>Olvida el token <b>si sigue siendo ése</b>: otra llamada pudo renovarlo ya, y botar el nuevo costaría un <c>/auth</c>
    /// de más.</summary>
    public void Invalidate(Guid connectionId, string staleToken)
    {
        if (_tokens.TryGetValue(connectionId, out var entry) && string.Equals(entry.Token, staleToken, StringComparison.Ordinal))
        {
            _tokens.TryRemove(new KeyValuePair<Guid, Entry>(connectionId, entry));
        }
    }

    private sealed record Entry(string Token, DateTimeOffset UsableUntil);
}
