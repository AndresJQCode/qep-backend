using Microsoft.AspNetCore.Routing;

namespace Modules.Pipeline.Api;

public static class PipelineEndpoints
{
    /// <summary>Se llena desde la Task 8, bajo <c>/api/v1/tenants/{tenantId:guid}/pipeline</c>.</summary>
    public static IEndpointRouteBuilder MapPipelineEndpoints(this IEndpointRouteBuilder endpoints) => endpoints;
}
