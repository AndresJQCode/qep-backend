using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Messaging.Application;

namespace Modules.Messaging.Api;

/// <summary>
/// Spec 2026-10-21 §5: los horarios de atención, bajo <c>/api/v1/tenants/{tenantId:guid}/messaging/business-hours</c>. Leer con
/// <c>messaging.conversation.read</c>; crear, editar, duplicar, borrar y volver predeterminado con <c>messaging.business_hours.manage</c>.
/// Editar, borrar y volver predeterminado piden <c>If-Match</c> (428 sin él, 412 con versión vieja); duplicar no, porque no cambia el origen.
/// Los handlers revalidan tenant, permiso y módulo.
/// </summary>
internal static class BusinessHoursEndpoints
{
    public static void MapBusinessHoursEndpoints(this RouteGroupBuilder messaging)
    {
        ArgumentNullException.ThrowIfNull(messaging);
        var group = messaging.MapGroup("/business-hours");

        group.MapGet("/", ListAsync)
            .RequireAuthorization(MessagingPermissions.ConversationRead)
            .Produces<BusinessHoursListDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/", CreateAsync)
            .RequireAuthorization(MessagingPermissions.BusinessHoursManage)
            .Accepts<SaveBusinessHoursRequest>("application/json")
            .Produces<BusinessHoursDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/{scheduleId:guid}", UpdateAsync)
            .RequireAuthorization(MessagingPermissions.BusinessHoursManage)
            .Accepts<SaveBusinessHoursRequest>("application/json")
            .Produces<BusinessHoursDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapDelete("/{scheduleId:guid}", DeleteAsync)
            .RequireAuthorization(MessagingPermissions.BusinessHoursManage)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapPost("/{scheduleId:guid}/duplicate", DuplicateAsync)
            .RequireAuthorization(MessagingPermissions.BusinessHoursManage)
            .Produces<BusinessHoursDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{scheduleId:guid}/default", SetDefaultAsync)
            .RequireAuthorization(MessagingPermissions.BusinessHoursManage)
            .Produces<BusinessHoursDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);
    }

    private static async Task<IResult> ListAsync(Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListBusinessHoursQuery(tenantId), cancellationToken));

    private static async Task<IResult> CreateAsync(
        Guid tenantId, SaveBusinessHoursRequest request, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var dto = await dispatcher.SendAsync(new CreateBusinessHoursCommand(tenantId, request), cancellationToken);
        return Results.Created($"{httpContext.Request.Path.Value!.TrimEnd('/')}/{dto.Id}", dto);
    }

    private static async Task<IResult> UpdateAsync(
        Guid tenantId, Guid scheduleId, SaveBusinessHoursRequest request, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new UpdateBusinessHoursCommand(tenantId, scheduleId, MessagingEndpoints.RequireVersion(httpContext), request), cancellationToken));

    private static async Task<IResult> DeleteAsync(
        Guid tenantId, Guid scheduleId, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(new DeleteBusinessHoursCommand(tenantId, scheduleId, MessagingEndpoints.RequireVersion(httpContext)), cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> DuplicateAsync(
        Guid tenantId, Guid scheduleId, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var dto = await dispatcher.SendAsync(new DuplicateBusinessHoursCommand(tenantId, scheduleId), cancellationToken);
        // La ruta es .../business-hours/{id}/duplicate: el recurso nuevo vive en .../business-hours/{newId}.
        var collection = httpContext.Request.Path.Value![..httpContext.Request.Path.Value!.LastIndexOf($"/{scheduleId}", StringComparison.OrdinalIgnoreCase)];
        return Results.Created($"{collection}/{dto.Id}", dto);
    }

    private static async Task<IResult> SetDefaultAsync(
        Guid tenantId, Guid scheduleId, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new SetDefaultBusinessHoursCommand(tenantId, scheduleId, MessagingEndpoints.RequireVersion(httpContext)), cancellationToken));
}
