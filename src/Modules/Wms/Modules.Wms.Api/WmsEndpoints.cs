using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Modules.Wms.Api;

public static class WmsEndpoints
{
    public static IEndpointRouteBuilder MapWmsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Tenant en la ruta: la política frena a quien no tiene el permiso y el handler (WmsAuthorization) a quien lo tiene en
        // otro tenant. Las rutas llegan con los casos de uso.
        endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/wms")
            .WithTags("Wms");

        return endpoints;
    }
}
