using System.Threading.RateLimiting;
using BuildingBlocks.Application;
using Bootstrapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Api;

/// <summary>
/// Spec 2026-10-30 §5.5: lo que recibe quien supera el límite por IP de la API externa. Bajo <c>/api/v1/external/</c> escribe un
/// ProblemDetails con el <c>code</c> del contrato y el <c>Retry-After</c>, igual que el límite por clave; en cualquier otra ruta
/// no hace nada y queda el 429 pelado de siempre.
/// </summary>
internal static class ExternalRateLimitRejection
{
    public static async ValueTask WriteAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var httpContext = context.HttpContext;
        if (!ExternalApiPaths.IsExternal(httpContext.Request.Path))
        {
            return;
        }

        httpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            httpContext.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title = "Too many requests",
            Detail = "Superaste el límite de peticiones. Espera unos segundos e intenta de nuevo.",
            Instance = httpContext.Request.Path,
        };
        problem.Extensions["code"] = ExternalApiErrorCodes.RateLimited;
        problem.Extensions["traceId"] = RequestFailureCapture.TraceIdOf(httpContext);
        await httpContext.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json", cancellationToken);
    }
}
