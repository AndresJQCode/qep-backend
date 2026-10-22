using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Automations.Application;

namespace Modules.Automations.Api;

/// <summary>Spec 2026-10-21 §4.4: <c>GET /runs?chatbotId=&amp;from=&amp;to=</c>, con <c>automations.chatbot.read</c>. Lo que el asesor ve
/// en el hilo no sale de acá sino de Messaging (§5.5).</summary>
internal static class RunEndpoints
{
    public static void MapRunEndpoints(this RouteGroupBuilder automations)
    {
        ArgumentNullException.ThrowIfNull(automations);
        automations.MapGet("/runs", ListAsync)
            .RequireAuthorization(AutomationsPermissions.ChatbotRead)
            .Produces<IReadOnlyList<RunDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    private static async Task<IResult> ListAsync(
        Guid tenantId, Guid chatbotId, DateTimeOffset? from, DateTimeOffset? to, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListRunsQuery(tenantId, chatbotId, from, to), cancellationToken));
}
