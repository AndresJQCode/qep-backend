using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Messaging.Application;

namespace Modules.Messaging.Api;

public static class MessagingEndpoints
{
    /// <summary>Lista, detalle e hilo (Task 14); envío, leído, resolver, reabrir, búsqueda y medio se suman
    /// en las siguientes, todos bajo <c>/api/v1/tenants/{tenantId:guid}/messaging</c>.</summary>
    public static IEndpointRouteBuilder MapMessagingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Tenant en la ruta; cada endpoint su política (spec 2026-10-09 §6.4). Los handlers revalidan
        // tenant, permiso y módulo, y responden 403 con código, nunca 404, ante otro tenant o módulo apagado.
        var group = endpoints.MapGroup("/api/v1/tenants/{tenantId:guid}/messaging").WithTags("Messaging");

        group.MapGet("/conversations", ListConversationsAsync)
            .RequireAuthorization(MessagingPermissions.ConversationRead)
            .Produces<ConversationPageDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/conversations/{conversationId:guid}", GetConversationAsync)
            .RequireAuthorization(MessagingPermissions.ConversationRead)
            .Produces<ConversationSummary>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/conversations/{conversationId:guid}/messages", ListMessagesAsync)
            .RequireAuthorization(MessagingPermissions.ConversationRead)
            .Produces<MessagePageDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    private static async Task<IResult> ListConversationsAsync(
        Guid tenantId, string? status, string? search, int? page, int? pageSize, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListConversationsQuery(tenantId, status, search, page, pageSize), cancellationToken));

    private static async Task<IResult> GetConversationAsync(Guid tenantId, Guid conversationId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetConversationQuery(tenantId, conversationId), cancellationToken));

    private static async Task<IResult> ListMessagesAsync(
        Guid tenantId, Guid conversationId, int? limit, Guid? before, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListMessagesQuery(tenantId, conversationId, limit, before), cancellationToken));
}
