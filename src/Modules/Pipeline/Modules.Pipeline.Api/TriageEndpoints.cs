using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Pipeline.Application;

namespace Modules.Pipeline.Api;

/// <summary>Spec 2026-10-13 §5.1 y §6: las dos decisiones de triage (D29). La lista de pendientes se quitó con el contador
/// «Sin negocio» de la bandeja, su único consumidor; el estado de una conversación lo da el bloque <c>triage</c> del resumen
/// del cliente.</summary>
internal static class TriageEndpoints
{
    public static void MapTriageEndpoints(this RouteGroupBuilder pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        var triage = pipeline.MapGroup("/triage");

        triage.MapPost("/{conversationId:guid}", DecideAsync)
            .RequireAuthorization(PipelinePermissions.DealManage)
            .Accepts<DecideTriageRequest>("application/json")
            .Produces<TriageResultDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    private static async Task<IResult> DecideAsync(
        Guid tenantId, Guid conversationId, DecideTriageRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new DecideTriageCommand(tenantId, conversationId, request.Action, request.Title, request.DismissReasonId), cancellationToken));
}

/// <summary>D29: <c>action</c> es <c>create</c> o <c>dismiss</c>. <c>title</c> opcional (sin él, el nombre del cliente).</summary>
public sealed record DecideTriageRequest(string? Action, string? Title, Guid? DismissReasonId);
