using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using Modules.ApiAccess.Application;

namespace Bootstrapper.ApiKeys;

/// <summary>
/// El límite por clave de la API externa (spec 2026-10-30 §5.5): ventana fija de un minuto, una partición por id de clave.
/// Singleton, porque el estado de las ventanas vive aquí. Solo se le piden ids de claves ya autenticadas, así que el número de
/// particiones lo acota el de claves reales; el limitador por IP es el que absorbe las inventadas.
/// </summary>
public sealed class ApiKeyRateLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<Guid> _limiter;

    public ApiKeyRateLimiter(IOptions<ApiAccessOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var permitLimit = options.Value.RateLimit.PerKeyPerMinute;
        _limiter = PartitionedRateLimiter.Create<Guid, Guid>(keyId =>
            RateLimitPartition.GetFixedWindowLimiter(
                keyId,
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
    }

    public ValueTask<RateLimitLease> AcquireAsync(Guid apiKeyId, CancellationToken cancellationToken) =>
        _limiter.AcquireAsync(apiKeyId, 1, cancellationToken);

    public void Dispose() => _limiter.Dispose();
}
