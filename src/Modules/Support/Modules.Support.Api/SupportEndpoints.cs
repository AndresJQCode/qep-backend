using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Modules.Support.Api;

public static class SupportEndpoints
{
    public static IEndpointRouteBuilder MapSupportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Tenant en la ruta, como loans: la política frena a quien no tiene el permiso y el handler
        // (SupportAuthorization) a quien lo tiene en otro tenant.
        endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/support")
            .WithTags("Support");

        return endpoints;
    }
}
