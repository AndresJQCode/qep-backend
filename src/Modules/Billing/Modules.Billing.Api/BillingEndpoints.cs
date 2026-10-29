using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Modules.Billing.Api;

public static class BillingEndpoints
{
    public static IEndpointRouteBuilder MapBillingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Tenant en la ruta, como suppliers y customers: la política frena a quien no tiene el
        // permiso y el handler (BillingAuthorization) a quien lo tiene en otro tenant. Las rutas
        // del módulo se agregan en las tareas siguientes.
        endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/billing")
            .WithTags("Billing");

        return endpoints;
    }
}
