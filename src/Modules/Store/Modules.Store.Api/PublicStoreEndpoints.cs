using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;

namespace Modules.Store.Api;

public static class PublicStoreEndpoints
{
    /// <summary>Tope del cuerpo de cualquier ruta pública: el carrito más grande cabe de sobra y el resto es abuso.</summary>
    private const long MaxRequestBodyBytes = 65_536;

    /// <param name="readPolicy">Limitador de las lecturas y del carrito (por IP del visitante).</param>
    /// <param name="submitPolicy">Limitador del envío de solicitudes: <c>order-requests</c> reemplaza <paramref name="readPolicy"/> por este.</param>
    public static IEndpointRouteBuilder MapPublicStoreEndpoints(
        this IEndpointRouteBuilder endpoints, string readPolicy, string submitPolicy)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(readPolicy);
        ArgumentException.ThrowIfNullOrWhiteSpace(submitPolicy);

        // Sin sesión: el host de la tienda va en la ruta y nunca se lee la cookie. X-Qep-Client y CSRF no aplican a las rutas
        // públicas (excepción por prefijo en RequireCsrfHeaderMiddleware); en su lugar, el filtro exige la clave del storefront.
        // Las rutas llegan con las tareas del catálogo público y del envío de solicitudes; la del envío usa submitPolicy.
        _ = endpoints
            .MapGroup("/api/v1/public/stores/{host}")
            .WithTags("Store")
            .AllowAnonymous()
            .AddEndpointFilter<StorefrontKeyFilter>()
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodyBytes))
            .RequireRateLimiting(readPolicy);

        return endpoints;
    }
}
