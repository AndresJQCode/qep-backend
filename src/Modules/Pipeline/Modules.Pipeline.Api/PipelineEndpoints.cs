using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Modules.Pipeline.Api;

public static class PipelineEndpoints
{
    /// <summary>Spec 2026-10-13 §6: todo bajo <c>/api/v1/tenants/{tenantId:guid}/pipeline</c>. Cada ruta declara su
    /// <c>RequireAuthorization</c>; los handlers revalidan tenant, permiso y capacidad (403 con código, nunca 404 ante otro tenant).</summary>
    public static IEndpointRouteBuilder MapPipelineEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/tenants/{tenantId:guid}/pipeline").WithTags("Pipeline");
        group.MapSettingsEndpoints();
        group.MapDealEndpoints();
        group.MapBoardEndpoints();
        group.MapTriageEndpoints();
        group.MapCustomerSummaryEndpoints();
        return endpoints;
    }
}
