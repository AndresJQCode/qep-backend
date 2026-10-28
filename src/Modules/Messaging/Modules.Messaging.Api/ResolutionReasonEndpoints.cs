using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Messaging.Application;

namespace Modules.Messaging.Api;

/// <summary>
/// Spec 2026-10-15 §3.4: el catálogo de motivos de resolución, bajo <c>/api/v1/tenants/{tenantId:guid}/messaging/resolution-reasons</c>.
/// Leer con <c>messaging.conversation.read</c>; renombrar y archivar con <c>messaging.resolution_reason.manage</c>. Sin <c>POST</c>: el
/// catálogo se alimenta desde el resolver (D-R4). Los handlers revalidan tenant, permiso y módulo.
/// </summary>
internal static class ResolutionReasonEndpoints
{
    public static void MapResolutionReasonEndpoints(this RouteGroupBuilder messaging)
    {
        ArgumentNullException.ThrowIfNull(messaging);
        var group = messaging.MapGroup("/resolution-reasons");

        group.MapGet("/", ListAsync)
            .RequireAuthorization(MessagingPermissions.ConversationRead)
            .Produces<IReadOnlyList<ResolutionReasonDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        // Con If-Match como resolve/reopen (428 sin él, 412 con versión vieja).
        group.MapPatch("/{reasonId:guid}", RenameAsync)
            .RequireAuthorization(MessagingPermissions.ResolutionReasonManage)
            .Accepts<RenameResolutionReasonRequest>("application/json")
            .Produces<ResolutionReasonDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapDelete("/{reasonId:guid}", ArchiveAsync)
            .RequireAuthorization(MessagingPermissions.ResolutionReasonManage)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed);
    }

    private static async Task<IResult> ListAsync(Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListResolutionReasonsQuery(tenantId), cancellationToken));

    private static async Task<IResult> RenameAsync(
        Guid tenantId, Guid reasonId, RenameResolutionReasonRequest request, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new RenameResolutionReasonCommand(tenantId, reasonId, request.Name, MessagingEndpoints.RequireVersion(httpContext)), cancellationToken));

    private static async Task<IResult> ArchiveAsync(Guid tenantId, Guid reasonId, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(new ArchiveResolutionReasonCommand(tenantId, reasonId), cancellationToken);
        return Results.NoContent();
    }
}

/// <summary>§3.4: el cuerpo del renombrar. Opcional para que un nombre ausente salga 422 con <c>errors.name</c>, no 400 del binder.</summary>
public sealed record RenameResolutionReasonRequest(string? Name);
