using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Modules.Store.Api;

public static class StoreEndpoints
{
    public static IEndpointRouteBuilder MapStoreEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Tenant en la ruta, como loans y billing: la política frena a quien no tiene el permiso y el handler
        // (StoreAuthorization) a quien lo tiene en otro tenant. Las rutas llegan con las tareas de ajustes y bandeja.
        _ = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/store")
            .WithTags("Store");

        endpoints.MapPublicStoreEndpoints();

        return endpoints;
    }
}
