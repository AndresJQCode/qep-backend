using System.Threading.RateLimiting;

namespace Bootstrapper.Valkey.RateLimiting;

/// <summary>
/// El lease de <see cref="ValkeyFixedWindowRateLimiter"/>. Un rechazo lleva <see cref="MetadataName.RetryAfter"/> con los
/// segundos que faltan para que cierre la ventana: lo leen <c>ExternalRateLimitRejection</c> y <c>ApiKeyRateLimitFilter</c>
/// para el header <c>Retry-After</c>, igual que con el lease del limitador de memoria.
/// </summary>
internal sealed class ValkeyRateLimitLease(bool isAcquired, TimeSpan? retryAfter) : RateLimitLease
{
    private static readonly string[] RetryAfterOnly = [MetadataName.RetryAfter.Name];

    public override bool IsAcquired => isAcquired;

    public override IEnumerable<string> MetadataNames => retryAfter is null ? [] : RetryAfterOnly;

    public override bool TryGetMetadata(string metadataName, out object? metadata)
    {
        if (retryAfter is { } value && metadataName == MetadataName.RetryAfter.Name)
        {
            metadata = value;
            return true;
        }

        metadata = null;
        return false;
    }
}
