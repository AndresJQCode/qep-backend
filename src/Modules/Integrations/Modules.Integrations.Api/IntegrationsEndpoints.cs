using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Modules.Integrations.Api;

public static class IntegrationsEndpoints
{
    public static IEndpointRouteBuilder MapIntegrationsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Tenant en la ruta, como pos y companies. Cada endpoint declara su propio
        // RequireAuthorization (spec 2026-10-08, «Endpoints»): el grupo no lleva política.
        endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/integrations")
            .WithTags("Integrations");

        return endpoints;
    }
}
