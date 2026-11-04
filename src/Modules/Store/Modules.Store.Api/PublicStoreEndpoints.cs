using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Modules.Store.Api;

internal static class PublicStoreEndpoints
{
    public static IEndpointRouteBuilder MapPublicStoreEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Sin sesión: el host de la tienda va en la ruta y nunca se lee la cookie. X-Qep-Client y CSRF no aplican a las rutas públicas.
        // Las rutas llegan con las tareas del catálogo público y del envío de solicitudes.
        _ = endpoints
            .MapGroup("/api/v1/public/stores/{host}")
            .WithTags("Store");

        return endpoints;
    }
}
