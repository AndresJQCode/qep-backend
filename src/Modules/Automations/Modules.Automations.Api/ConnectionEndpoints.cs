using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Automations.Application;

namespace Modules.Automations.Api;

/// <summary>Spec 2026-10-21 §4.4: <c>GET /connections</c>, con <c>automations.chatbot.read</c>.</summary>
internal static class ConnectionEndpoints
{
    public static void MapConnectionEndpoints(this RouteGroupBuilder automations)
    {
        ArgumentNullException.ThrowIfNull(automations);
        automations.MapGet("/connections", ListAsync)
            .RequireAuthorization(AutomationsPermissions.ChatbotRead)
            .Produces<IReadOnlyList<SelectableConnectionDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden);
    }

    private static async Task<IResult> ListAsync(Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListSelectableConnectionsQuery(tenantId), cancellationToken));
}
