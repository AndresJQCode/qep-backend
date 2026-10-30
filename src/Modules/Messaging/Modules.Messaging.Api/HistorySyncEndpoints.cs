using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Messaging.Application;

namespace Modules.Messaging.Api;

/// <summary>
/// Spec 2026-10-29 §5: el estado de la importación del historial por coexistencia de una conexión, bajo
/// <c>/api/v1/tenants/{tenantId:guid}/messaging/connections/{connectionId:guid}/history-sync</c>. Leer con
/// <c>integrations.connection.read</c>; reintentar con <c>integrations.connection.manage</c> (P-8). Los handlers revalidan tenant y
/// permiso, pero no el módulo de Messaging.
/// </summary>
internal static class HistorySyncEndpoints
{
    public static void MapHistorySyncEndpoints(this RouteGroupBuilder messaging)
    {
        ArgumentNullException.ThrowIfNull(messaging);
        var group = messaging.MapGroup("/connections/{connectionId:guid}/history-sync");

        group.MapGet("/", GetAsync)
            .RequireAuthorization(MessagingConnectionPermissions.Read)
            .Produces<HistorySyncDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Sin cuerpo: devuelve el estado después del reintento; el pedido sale en la próxima pasada del worker.
        group.MapPost("/retry", RetryAsync)
            .RequireAuthorization(MessagingConnectionPermissions.Manage)
            .Produces<HistorySyncDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            // concurrency.conflict (un cambio del worker entre la lectura y el guardado) es 412 en ApiExceptionHandler.
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    private static async Task<IResult> GetAsync(Guid tenantId, Guid connectionId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetHistorySyncQuery(tenantId, connectionId), cancellationToken));

    private static async Task<IResult> RetryAsync(Guid tenantId, Guid connectionId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new RetryHistorySyncCommand(tenantId, connectionId), cancellationToken));
}
