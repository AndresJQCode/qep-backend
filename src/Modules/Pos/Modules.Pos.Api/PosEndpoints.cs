using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Modules.Pos.Api;

public static class PosEndpoints
{
    public static IEndpointRouteBuilder MapPosEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Tenant en la ruta, como companies y catalog. Cada endpoint declara su propio
        // RequireAuthorization (spec, «API»): el grupo no lleva política.
        endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/pos")
            .WithTags("Pos");

        return endpoints;
    }
}
