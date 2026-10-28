using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Automations.Application;

namespace Modules.Automations.Api;

/// <summary>Spec 2026-10-21 §4.4 y §8: el simulador del editor, todo con <c>automations.chatbot.manage</c> (probar un flujo puede llamar a
/// una API real, D-S2). Sin <c>If-Match</c>: la simulación no tiene versión, es del panel que la abrió.</summary>
internal static class SimulationEndpoints
{
    public static void MapSimulationEndpoints(this RouteGroupBuilder automations)
    {
        ArgumentNullException.ThrowIfNull(automations);

        automations.MapPost("/chatbots/{chatbotId:guid}/simulations", StartAsync)
            .RequireAuthorization(AutomationsPermissions.ChatbotManage)
            .Accepts<StartSimulationRequest>("application/json")
            .Produces<SimulationDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        automations.MapPost("/simulations/{simulationId:guid}/messages", SendAsync)
            .RequireAuthorization(AutomationsPermissions.ChatbotManage)
            .Accepts<SimulationMessageRequest>("application/json")
            .Produces<SimulationDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        automations.MapDelete("/simulations/{simulationId:guid}", DeleteAsync)
            .RequireAuthorization(AutomationsPermissions.ChatbotManage)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    // Sin Location, como crear un chatbot: el panel pinta la transcripción que vuelve y no relee la simulación.
    private static async Task<IResult> StartAsync(
        Guid tenantId, Guid chatbotId, StartSimulationRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Created(
            (string?)null, await dispatcher.SendAsync(new StartSimulationCommand(tenantId, chatbotId, request.LiveHttp), cancellationToken));

    private static async Task<IResult> SendAsync(
        Guid tenantId, Guid simulationId, SimulationMessageRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new SendSimulationMessageCommand(tenantId, simulationId, request.Text, request.ReplyId, request.Attachment), cancellationToken));

    private static async Task<IResult> DeleteAsync(Guid tenantId, Guid simulationId, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(new DeleteSimulationCommand(tenantId, simulationId), cancellationToken);
        return Results.NoContent();
    }
}

/// <summary>D-S2: <see cref="LiveHttp"/> apagado por defecto; prendido, el bloque HTTP llama a la API de la conexión.</summary>
public sealed record StartSimulationRequest(bool LiveHttp);

/// <summary>§8: texto escrito, el <c>replyId</c> de un botón o una fila, o un adjunto ficticio (<c>{ fileName, mimeType }</c>).</summary>
public sealed record SimulationMessageRequest(string? Text, string? ReplyId, SimulationAttachmentRequest? Attachment);
