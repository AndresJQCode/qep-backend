using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Messaging.Application;

namespace Modules.Messaging.Api;

/// <summary>
/// Spec 2026-10-14 §4.7: la administración de plantillas de una conexión, bajo
/// <c>/api/v1/tenants/{tenantId:guid}/messaging/connections/{connectionId:guid}/template-management</c>, toda con
/// <c>messaging.template.manage</c>. Los handlers revalidan tenant, permiso, módulo y conexión (403 con código, nunca 404 ante otro tenant;
/// 422 <c>connection_unavailable</c> ante una conexión ajena).
/// </summary>
internal static class TemplateManagementEndpoints
{
    public static void MapTemplateManagementEndpoints(this RouteGroupBuilder messaging)
    {
        ArgumentNullException.ThrowIfNull(messaging);
        var group = messaging.MapGroup("/connections/{connectionId:guid}/template-management");

        group.MapGet("/", GetAsync)
            .RequireAuthorization(MessagingPermissions.TemplateManage)
            .Produces<TemplateManagementDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/sync", SyncAsync)
            .RequireAuthorization(MessagingPermissions.TemplateManage)
            .Produces<TemplateManagementDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    private static async Task<IResult> GetAsync(Guid tenantId, Guid connectionId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetTemplateManagementQuery(tenantId, connectionId), cancellationToken));

    private static async Task<IResult> SyncAsync(Guid tenantId, Guid connectionId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new SyncTemplatesCommand(tenantId, connectionId), cancellationToken));
}
