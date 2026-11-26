using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Wms.Application;

namespace Modules.Wms.Api;

/// <summary>Carga inicial de saldos por Excel (RF-024, spec WMS 2026-10-31 §24.3). El preview responde 200 con los problemas por fila y
/// el commit es IK (header <c>Idempotency-Key</c>): un preview con problemas no es un error del request, confirmarlo sí.</summary>
internal static class ImportEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapPost("/imports/initial-balances/preview", PreviewAsync)
            .RequireAuthorization(WmsPermissions.AdjustmentPropose)
            .Produces<InitialBalancePreviewDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .DisableAntiforgery();

        group.MapPost("/imports/initial-balances/commit", CommitAsync)
            .RequireIdempotencyKey()
            .RequireAuthorization(WmsPermissions.AdjustmentPropose)
            .Accepts<CommitInitialBalancesRequest>("application/json")
            .Produces<InitialBalanceCommitDto>(StatusCodes.Status201Created)
            .Produces<InitialBalanceCommitDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);
    }

    private static async Task<IResult> PreviewAsync(
        Guid tenantId,
        Guid warehouseId,
        Guid? ownerId,
        IFormFile file,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        // El handler consume el stream completo antes de devolver, así que sigue vivo durante toda la llamada.
        await using var content = file.OpenReadStream();
        var preview = await dispatcher.SendAsync(
            new PreviewInitialBalancesCommand(tenantId, warehouseId, ownerId, file.FileName, file.Length, content), cancellationToken);
        return Results.Ok(preview);
    }

    private static async Task<IResult> CommitAsync(
        Guid tenantId, CommitInitialBalancesRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var committed = await dispatcher.SendAsync(
            new CommitInitialBalancesCommand(tenantId, request.PreviewId, request.Version), cancellationToken);

        // Como los ajustes: no hay GET por id, el Location apunta al listado.
        return Results.Created($"/api/v1/tenants/{tenantId}/wms/adjustments", committed);
    }
}
