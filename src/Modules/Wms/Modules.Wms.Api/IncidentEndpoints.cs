using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Wms.Application;

namespace Modules.Wms.Api;

/// <summary>Las novedades (spec 2026-11-26 §8.5). Se leen con <c>wms.document.read</c> y se declaran sueltas con la misma política
/// más el permiso de algún tipo de tarea en el handler (DA-49). <c>POST /incidents</c> es IK (header <c>Idempotency-Key</c>).</summary>
internal static class IncidentEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/incidents", ListAsync)
            .RequireAuthorization(WmsPermissions.DocumentRead)
            .Produces<IncidentPageDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/incidents", ReportAsync)
            .RequireIdempotencyKey()
            .RequireAuthorization(WmsPermissions.DocumentRead)
            .Accepts<ReportIncidentRequest>("application/json")
            .Produces<IncidentDto>(StatusCodes.Status201Created)
            .Produces<IncidentDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapGet("/incidents/{id:guid}", GetAsync)
            .RequireAuthorization(WmsPermissions.DocumentRead)
            .Produces<IncidentDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Las tres acciones del supervisor llevan la versión que leyó (412 si cambió). resolve es SK: la clave la deriva el servidor
        // (incident:{id}:resolve:{version}) y repetirla devuelve lo guardado.
        group.MapPost("/incidents/{id:guid}/review", ReviewAsync)
            .RequireAuthorization(WmsPermissions.DiscrepancyResolve)
            .Accepts<IncidentVersionRequest>("application/json")
            .Produces<IncidentDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/incidents/{id:guid}/resolve", ResolveAsync)
            .UseServerIdempotencyKey("incident", "resolve")
            .RequireAuthorization(WmsPermissions.DiscrepancyResolve)
            .Accepts<ResolveIncidentRequest>("application/json")
            .Produces<IncidentResolutionDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/incidents/{id:guid}/reject", RejectAsync)
            .RequireAuthorization(WmsPermissions.DiscrepancyResolve)
            .Accepts<RejectIncidentRequest>("application/json")
            .Produces<IncidentDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    private static async Task<IResult> ListAsync(
        Guid tenantId,
        Guid? warehouseId,
        string? status,
        string? type,
        int? page,
        int? pageSize,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(
            new ListIncidentsQuery(tenantId, warehouseId, status, type, page ?? 1, pageSize ?? WmsPaging.DefaultPageSize),
            cancellationToken));

    private static async Task<IResult> ReportAsync(
        Guid tenantId, ReportIncidentRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var incident = await dispatcher.SendAsync(ReportIncidentCommand.From(tenantId, request), cancellationToken);
        return Results.Created($"/api/v1/tenants/{tenantId}/wms/incidents/{incident.Id}", incident);
    }

    private static async Task<IResult> GetAsync(
        Guid tenantId, Guid id, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetIncidentQuery(tenantId, id), cancellationToken));

    private static async Task<IResult> ReviewAsync(
        Guid tenantId, Guid id, IncidentVersionRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new ReviewIncidentCommand(tenantId, id, request.Version), cancellationToken));

    private static async Task<IResult> ResolveAsync(
        Guid tenantId, Guid id, ResolveIncidentRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(ResolveIncidentCommand.From(tenantId, id, request), cancellationToken));

    private static async Task<IResult> RejectAsync(
        Guid tenantId, Guid id, RejectIncidentRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(RejectIncidentCommand.From(tenantId, id, request), cancellationToken));
}
