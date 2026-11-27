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
        // otro tenant. Cada archivo de endpoints cuelga sus rutas de este grupo, como Pipeline.
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/wms")
            .WithTags("Wms");

        WarehouseEndpoints.Map(group);
        LocationEndpoints.Map(group);
        OwnerEndpoints.Map(group);
        UomEndpoints.Map(group);
        VariantProfileEndpoints.Map(group);
        StockEndpoints.Map(group);
        AdjustmentEndpoints.Map(group);
        ImportEndpoints.Map(group);
        TaskEndpoints.Map(group);
        IncidentEndpoints.Map(group);

        return endpoints;
    }
}
