using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Pipeline.Application;

namespace Modules.Pipeline.Api;

/// <summary>Spec 2026-10-13 §6: el tablero. <c>owner=me</c> por defecto (D23: foco, no seguridad).</summary>
internal static class BoardEndpoints
{
    public static void MapBoardEndpoints(this RouteGroupBuilder pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        pipeline.MapGet("/board", GetBoardAsync)
            .RequireAuthorization(PipelinePermissions.DealRead)
            .Produces<PipelineBoardDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    private static async Task<IResult> GetBoardAsync(Guid tenantId, string? owner, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetBoardQuery(tenantId, owner), cancellationToken));
}
