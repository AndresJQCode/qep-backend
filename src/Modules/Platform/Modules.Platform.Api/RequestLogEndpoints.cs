using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Platform.Application;

namespace Modules.Platform.Api;

public static class RequestLogEndpoints
{
    public static IEndpointRouteBuilder MapPlatformEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/platform")
            .WithTags("Platform");

        group.MapGet("/request-log", ListRequestFailuresAsync)
            .RequireAuthorization(PlatformPermissions.RequestLogRead)
            .Produces<RequestLogPageResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        // DELETE y no POST: lo que hace es borrar un subconjunto de la coleccion, y el verbo lo
        // dice mejor que cualquier nombre de accion. Sin cuerpo ni parametros a proposito -- la
        // ventana que se conserva la fija el backend (PurgeRequestFailuresHandler.RetentionDays):
        // un endpoint que acepta la fecha de corte acepta tambien la de mañana, que es un borrado
        // total disfrazado de mantenimiento.
        group.MapDelete("/request-log", PurgeRequestFailuresAsync)
            .RequireAuthorization(PlatformPermissions.RequestLogPurge)
            .Produces<RequestLogPurgeResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    private static async Task<IResult> ListRequestFailuresAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        string? module = null,
        string? errorCode = null,
        DateTimeOffset? occurredFrom = null,
        DateTimeOffset? occurredTo = null,
        int page = 1,
        int pageSize = RequestFailurePaging.DefaultPageSize)
    {
        var result = await dispatcher.QueryAsync(
            new ListRequestFailuresQuery(
                tenantId, module, errorCode, occurredFrom, occurredTo, page, pageSize),
            cancellationToken);

        return Results.Ok(new RequestLogPageResponse(
            result.Items,
            result.Total,
            result.Page,
            result.PageSize,
            result.Modules,
            result.ErrorCodes));
    }

    private static async Task<IResult> PurgeRequestFailuresAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.SendAsync(
            new PurgeRequestFailuresCommand(tenantId), cancellationToken);

        // 200 con el conteo y no 204: "listo" sin numero no distingue "borre 4.000 filas" de "no
        // habia nada", y son dos cosas distintas para quien aprieta el boton.
        return Results.Ok(new RequestLogPurgeResponse(
            result.DeletedCount, result.OlderThan));
    }
}

/// <summary>El sobre del log, con los valores presentes para los dos desplegables del filtro.</summary>
public sealed record RequestLogPageResponse(
    IReadOnlyCollection<RequestFailureDto> Items,
    int Total,
    int Page,
    int PageSize,
    IReadOnlyCollection<string> Modules,
    IReadOnlyCollection<string> ErrorCodes);

public sealed record RequestLogPurgeResponse(int DeletedCount, DateTimeOffset OlderThan);
