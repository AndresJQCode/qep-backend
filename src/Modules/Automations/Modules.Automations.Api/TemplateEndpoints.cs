using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Automations.Application;

namespace Modules.Automations.Api;

/// <summary>Spec 2026-10-21 §4.4, D-C8: el catálogo de plantillas (<c>automations.chatbot.read</c>), guardar un chatbot como plantilla
/// y borrar una del tenant (<c>automations.chatbot.manage</c>). Crear un chatbot desde una plantilla es <c>POST /chatbots</c> con
/// <c>source.kind = template</c>.</summary>
internal static class TemplateEndpoints
{
    public static void MapTemplateEndpoints(this RouteGroupBuilder automations)
    {
        ArgumentNullException.ThrowIfNull(automations);

        automations.MapGet("/templates", ListAsync)
            .RequireAuthorization(AutomationsPermissions.ChatbotRead)
            .Produces<TemplateCatalogDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        // Sin :guid: el id de una plantilla del sistema también llega a esta ruta, y es 404 not_found, no una ruta que no existe.
        automations.MapDelete("/templates/{templateId}", DeleteAsync)
            .RequireAuthorization(AutomationsPermissions.ChatbotManage)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        automations.MapPost("/chatbots/{chatbotId:guid}/save-as-template", SaveAsTemplateAsync)
            .RequireAuthorization(AutomationsPermissions.ChatbotManage)
            .Accepts<SaveAsTemplateRequest>("application/json")
            .Produces<TemplateDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    private static async Task<IResult> ListAsync(Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListChatbotTemplatesQuery(tenantId), cancellationToken));

    private static async Task<IResult> DeleteAsync(Guid tenantId, string templateId, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(new DeleteChatbotTemplateCommand(tenantId, templateId), cancellationToken);
        return Results.NoContent();
    }

    // Sin Location, como crear un chatbot: la galería pinta la tarjeta que vuelve.
    private static async Task<IResult> SaveAsTemplateAsync(
        Guid tenantId, Guid chatbotId, SaveAsTemplateRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Created(
            (string?)null,
            await dispatcher.SendAsync(
                new SaveChatbotAsTemplateCommand(tenantId, chatbotId, request.Name, request.Category, request.Description), cancellationToken));
}

/// <summary>§4.4: <c>category</c> es <c>Support</c>, <c>Sales</c>, <c>Orders</c> o <c>TechnicalService</c>.</summary>
public sealed record SaveAsTemplateRequest(string Name, string Category, string Description);
