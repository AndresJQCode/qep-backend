using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Modules.Scheduling.Api;

public static class SchedulingEndpoints
{
    public static IEndpointRouteBuilder MapSchedulingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Tenant en la ruta, como hr y loans: la política frena a quien no tiene el permiso y el handler
        // (SchedulingAuthorization) a quien lo tiene en otro tenant. Las rutas llegan con los casos de uso.
        _ = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/scheduling")
            .WithTags("Scheduling");

        return endpoints;
    }
}
