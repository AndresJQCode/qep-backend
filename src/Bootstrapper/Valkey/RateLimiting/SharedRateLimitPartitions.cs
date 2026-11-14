using System.Threading.RateLimiting;

namespace Bootstrapper.Valkey.RateLimiting;

/// <summary>
/// La fábrica de particiones de las ventanas fijas (spec 2026-11-13, D-3): las siete políticas de <c>Program.cs</c> y el
/// límite por clave de la API externa. Los números siguen donde estaban; acá sólo se decide dónde se cuentan. Con
/// <c>Valkey:Areas:RateLimiting</c> apagado devuelve exactamente la partición de memoria de antes (V3 del plan); prendido,
/// un <see cref="ValkeyFixedWindowRateLimiter"/> que cuenta en Valkey entre todos los pods y vuelve al de memoria si Valkey
/// falla. El limitador de concurrencia del webhook no pasa por acá: protege al pod y se queda en memoria.
/// </summary>
public sealed class SharedRateLimitPartitions
{
    /// <summary>La política del límite por clave de la API externa en la clave de Valkey (<c>qep:rl:apikey:{id}:…</c>).</summary>
    public const string ApiKeyPolicy = "apikey";

    private readonly ValkeyGateway _gateway;
    private readonly TimeProvider _time;
    private readonly bool _shared;

    internal SharedRateLimitPartitions(ValkeyGateway gateway, TimeProvider time)
    {
        _gateway = gateway;
        _time = time;
        _shared = gateway.IsEnabled(ValkeyArea.RateLimiting);
    }

    /// <summary>
    /// La partición <paramref name="partitionKey"/> de <paramref name="policy"/>. Con <paramref name="hashPartition"/> la
    /// partición entra a la clave de Valkey como <see cref="ValkeyKeys.ShortHash"/> (V7): las que llevan patrón de ruta no
    /// cargan texto libre en la clave. La partición en memoria sigue usando la clave sin hashear.
    /// </summary>
    public RateLimitPartition<string> FixedWindow(
        string policy, string partitionKey, FixedWindowRateLimiterOptions options, bool hashPartition = false)
    {
        if (!_shared)
        {
            return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => options);
        }

        return RateLimitPartition.Get(
            partitionKey,
            key => new ValkeyFixedWindowRateLimiter(
                _gateway, policy, hashPartition ? ValkeyKeys.ShortHash(key) : key, options, _time));
    }
}
