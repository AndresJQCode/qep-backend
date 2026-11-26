using System.Threading.RateLimiting;
using Bootstrapper.Valkey.RateLimiting;
using Microsoft.Extensions.Options;
using Modules.ApiAccess.Application;

namespace Bootstrapper.ApiKeys;

/// <summary>
/// El límite por clave de la API externa (spec 2026-10-30 §6.1): ventana fija de un minuto, una partición por id de clave.
/// Singleton, porque las particiones viven aquí. Solo se le piden ids de claves ya autenticadas, así que el número de
/// particiones lo acota el de claves reales; el limitador por IP es el que absorbe las inventadas. Con
/// <c>Valkey:Areas:RateLimiting</c> la ventana se cuenta en Valkey y vale para todos los pods (spec 2026-11-13, D-3).
/// </summary>
public sealed class ApiKeyRateLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<Guid> _limiter;

    public ApiKeyRateLimiter(IOptions<ApiAccessOptions> options, SharedRateLimitPartitions partitions)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(partitions);
        var window = new FixedWindowRateLimiterOptions
        {
            PermitLimit = options.Value.RateLimit.PerKeyPerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        };
        _limiter = PartitionedRateLimiter.Create<Guid, string>(keyId =>
            partitions.FixedWindow(SharedRateLimitPartitions.ApiKeyPolicy, keyId.ToString("D"), window));
    }

    public ValueTask<RateLimitLease> AcquireAsync(Guid apiKeyId, CancellationToken cancellationToken) =>
        _limiter.AcquireAsync(apiKeyId, 1, cancellationToken);

    public void Dispose() => _limiter.Dispose();
}
