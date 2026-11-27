using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Modules.Siigo.Application;

namespace Modules.Siigo.Infrastructure.Api;

/// <summary>
/// Spec 2026-11-18 §7: el token de Siigo por conexión, en memoria, hasta <c>expires_in</c> menos un margen de
/// <see cref="Margin"/>. Con varias réplicas cada pod tiene el suyo (un token de más por réplica no tiene efecto: Siigo acepta varios
/// vivos). Singleton: se clasifica en <c>MultiReplicaInventory</c>. El token nunca sale de aquí a un log, una auditoría o una
/// respuesta. El reloj es el <see cref="TimeProvider"/> (no <c>IClock</c>, que es scoped y un singleton no puede consumirlo), para probar el vencimiento.
/// <para>
/// Cada entrada guarda la huella de las credenciales con las que se pidió el token, y sólo se sirve a esas mismas. Editar la conexión
/// con el usuario y la clave de otra cuenta de Siigo deja el mismo <c>connectionId</c>: sin la huella se seguía usando el token de la
/// cuenta anterior (unas 24 h) y el asistente mostraba sus listas. Se compara en cada llamada, en cualquier pod, así que no depende
/// de que el pod que atendió el cambio avise a los demás.
/// </para>
/// </summary>
internal sealed class SiigoTokenCache(TimeProvider clock)
{
    /// <summary>Se renueva un poco antes de que venza: una llamada larga no debería arrancar con un token a segundos de caducar.</summary>
    public static readonly TimeSpan Margin = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<Guid, Entry> _tokens = new();

    /// <summary>El token vigente de la conexión pedido con estas mismas credenciales, o <c>null</c> si no hay, ya entró en el margen o
    /// las credenciales cambiaron.</summary>
    public string? TryGet(SiigoCredentials credentials) =>
        _tokens.TryGetValue(credentials.ConnectionId, out var entry)
        && entry.UsableUntil > clock.GetUtcNow()
        && entry.Fingerprint == Fingerprint(credentials)
            ? entry.Token
            : null;

    public void Set(SiigoCredentials credentials, string token, TimeSpan lifetime) =>
        _tokens[credentials.ConnectionId] = new Entry(token, Fingerprint(credentials), clock.GetUtcNow() + lifetime - Margin);

    /// <summary>Olvida el token <b>si sigue siendo ése y de estas credenciales</b>: otra llamada pudo renovarlo ya, y botar el nuevo
    /// costaría un <c>/auth</c> de más.</summary>
    public void Invalidate(SiigoCredentials credentials, string staleToken)
    {
        if (_tokens.TryGetValue(credentials.ConnectionId, out var entry)
            && string.Equals(entry.Token, staleToken, StringComparison.Ordinal)
            && entry.Fingerprint == Fingerprint(credentials))
        {
            _tokens.TryRemove(new KeyValuePair<Guid, Entry>(credentials.ConnectionId, entry));
        }
    }

    // SHA-256 del usuario y la clave: la clave en claro no se guarda aquí. El largo del usuario va delante para que pasar caracteres
    // del usuario a la clave ("ab" + "c" contra "a" + "bc") no dé la misma huella.
    private static string Fingerprint(SiigoCredentials credentials) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{credentials.Username.Length}:{credentials.Username}{credentials.AccessKey}")));

    private sealed record Entry(string Token, string Fingerprint, DateTimeOffset UsableUntil);
}
