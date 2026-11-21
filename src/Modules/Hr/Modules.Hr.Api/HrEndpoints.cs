using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Modules.Hr.Api;

public static class HrEndpoints
{
    public static IEndpointRouteBuilder MapHrEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Tenant en la ruta, como loans y billing: la política frena a quien no tiene el permiso y el handler
        // (HrAuthorization) a quien lo tiene en otro tenant. Las rutas llegan con las Tareas 3 y 6 a 10.
        endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/hr")
            .WithTags("Hr");

        return endpoints;
    }
}
