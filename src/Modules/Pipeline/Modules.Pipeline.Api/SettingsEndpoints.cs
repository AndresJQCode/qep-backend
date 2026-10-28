using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Pipeline.Application;
using Modules.Pipeline.Domain;

namespace Modules.Pipeline.Api;

/// <summary>Spec 2026-10-13 §6: los ajustes del embudo. Leer con <c>pipeline.deal.read</c> (el tablero necesita las etapas y el
/// modal de pérdida, los motivos); cambiar con <c>pipeline.settings.manage</c>.</summary>
internal static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this RouteGroupBuilder pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        var settings = pipeline.MapGroup("/settings");

        settings.MapGet("/stages", GetStagesAsync)
            .RequireAuthorization(PipelinePermissions.DealRead)
            .Produces<PipelineStagesDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        settings.MapPut("/stages", ReplaceStagesAsync)
            .RequireAuthorization(PipelinePermissions.SettingsManage)
            .Accepts<ReplaceStagesRequest>("application/json")
            .Produces<PipelineStagesDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        settings.MapGet("/loss-reasons", (Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                GetReasonsAsync(tenantId, ReasonKind.Loss, dispatcher, cancellationToken))
            .RequireAuthorization(PipelinePermissions.DealRead)
            .Produces<ReasonListDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        settings.MapPut("/loss-reasons", (Guid tenantId, ReplaceReasonsRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                ReplaceReasonsAsync(tenantId, ReasonKind.Loss, request, dispatcher, cancellationToken))
            .RequireAuthorization(PipelinePermissions.SettingsManage)
            .Accepts<ReplaceReasonsRequest>("application/json")
            .Produces<ReasonListDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        settings.MapGet("/dismiss-reasons", (Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                GetReasonsAsync(tenantId, ReasonKind.Dismiss, dispatcher, cancellationToken))
            .RequireAuthorization(PipelinePermissions.DealRead)
            .Produces<ReasonListDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        settings.MapPut("/dismiss-reasons", (Guid tenantId, ReplaceReasonsRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                ReplaceReasonsAsync(tenantId, ReasonKind.Dismiss, request, dispatcher, cancellationToken))
            .RequireAuthorization(PipelinePermissions.SettingsManage)
            .Accepts<ReplaceReasonsRequest>("application/json")
            .Produces<ReasonListDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    private static async Task<IResult> GetStagesAsync(Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetPipelineStagesQuery(tenantId), cancellationToken));

    private static async Task<IResult> ReplaceStagesAsync(
        Guid tenantId, ReplaceStagesRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new ReplacePipelineStagesCommand(tenantId, request.Version, request.Stages), cancellationToken));

    private static async Task<IResult> GetReasonsAsync(Guid tenantId, ReasonKind kind, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetPipelineReasonsQuery(tenantId, kind), cancellationToken));

    private static async Task<IResult> ReplaceReasonsAsync(
        Guid tenantId, ReasonKind kind, ReplaceReasonsRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new ReplacePipelineReasonsCommand(tenantId, kind, request.Items), cancellationToken));
}

/// <summary>§6: la disposición entera y ordenada con la versión del embudo. Todo opcional: lo que falte sale 422 con su clave.</summary>
public sealed record ReplaceStagesRequest(long? Version, IReadOnlyList<StageRequest>? Stages);

/// <summary>P8 del plan: el catálogo entero, sin versión.</summary>
public sealed record ReplaceReasonsRequest(IReadOnlyList<ReasonRequest>? Items);
