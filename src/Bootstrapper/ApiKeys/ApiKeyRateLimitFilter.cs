using System.Threading.RateLimiting;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Http;

namespace Bootstrapper.ApiKeys;

/// <summary>
/// Cobra una petición al cupo de la clave que llama (spec 2026-10-30 §5.5). Corre después de autenticar, así que conoce el id de
/// la clave; sin cupo lanza <see cref="RequestRateLimitedException"/>, que <c>ApiExceptionHandler</c> responde 429 con
/// <c>Retry-After</c>.
/// </summary>
public sealed class ApiKeyRateLimitFilter(ApiKeyRateLimiter limiter, IApiCaller caller) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        using (var lease = await limiter.AcquireAsync(caller.ApiKeyId, context.HttpContext.RequestAborted))
        {
            if (!lease.IsAcquired)
            {
                TimeSpan? retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? wait : null;
                throw new RequestRateLimitedException(
                    ExternalApiErrorCodes.RateLimited,
                    "Superaste el límite de peticiones de esta clave. Espera unos segundos e intenta de nuevo.",
                    retryAfter);
            }
        }

        return await next(context);
    }
}
