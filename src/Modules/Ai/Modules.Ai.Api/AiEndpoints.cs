using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Ai.Application;

namespace Modules.Ai.Api;

public static class AiEndpoints
{
    /// <summary>Spec 2026-10-29 §5: todo bajo <c>/api/v1/tenants/{tenantId:guid}/ai</c>. Cada ruta declara su <c>RequireAuthorization</c>;
    /// los handlers revalidan tenant, permiso y capacidad <c>ai</c> (403 con código, nunca 404 ante otro tenant).</summary>
    public static IEndpointRouteBuilder MapAiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/tenants/{tenantId:guid}/ai").WithTags("Ai");

        group.MapGet("/models", ListModelsAsync)
            .RequireAuthorization(AiPermissions.AgentRead)
            .Produces<AiModelCatalogDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/settings", GetSettingsAsync)
            .RequireAuthorization(AiPermissions.AgentRead)
            .Produces<AiSettingsDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    private static async Task<IResult> GetSettingsAsync(Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetAiSettingsQuery(tenantId), cancellationToken));

    private static async Task<IResult> ListModelsAsync(Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListAiModelsQuery(tenantId), cancellationToken));
}
