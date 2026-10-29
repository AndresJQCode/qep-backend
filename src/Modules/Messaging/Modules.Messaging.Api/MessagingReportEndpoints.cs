using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Messaging.Application;

namespace Modules.Messaging.Api;

/// <summary>
/// Spec 2026-10-28: el reporte de mensajería bajo <c>/api/v1/tenants/{tenantId:guid}/messaging/reports</c>. Lee con
/// <c>messaging.conversation.read</c> y el módulo <c>messaging</c>; el handler revalida tenant, permiso y módulo.
/// </summary>
internal static class MessagingReportEndpoints
{
    public static void MapMessagingReportEndpoints(this RouteGroupBuilder messaging)
    {
        ArgumentNullException.ThrowIfNull(messaging);
        var group = messaging.MapGroup("/reports");

        group.MapGet("/summary", SummaryAsync)
            .RequireAuthorization(MessagingPermissions.ConversationRead)
            .Produces<MessagingReportSummaryDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    private static async Task<IResult> SummaryAsync(
        Guid tenantId, DateOnly? from, DateOnly? to, string? groupBy, string? channel, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetMessagingReportSummaryQuery(tenantId, from, to, groupBy, channel), cancellationToken));
}
