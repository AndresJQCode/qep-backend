using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Messaging.Application;

namespace Modules.Messaging.Api;

public static class MessagingEndpoints
{
    /// <summary>Lista, detalle e hilo (Task 14), búsqueda (Task 15) y envío (Task 16); leído, resolver, reabrir y medio
    /// se suman en las siguientes, todos bajo <c>/api/v1/tenants/{tenantId:guid}/messaging</c>.</summary>
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

        // Spec 2026-10-09 §8.3: envío de texto, idempotente por clientId.
        group.MapPost("/conversations/{conversationId:guid}/messages", SendMessageAsync)
            .RequireAuthorization(MessagingPermissions.ConversationManage)
            .Accepts<SendMessageRequest>("application/json")
            .Produces<MessageDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/messages/search", SearchMessagesAsync)
            .RequireAuthorization(MessagingPermissions.ConversationRead)
            .Produces<SearchPageDto>()
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

    private static async Task<IResult> SearchMessagesAsync(
        Guid tenantId, string? q, Guid? conversationId, DateTimeOffset? from, DateTimeOffset? to, int? limit, Guid? before,
        IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new SearchMessagesQuery(tenantId, q, conversationId, from, to, limit, before), cancellationToken));

    private static async Task<IResult> SendMessageAsync(
        Guid tenantId, Guid conversationId, SendMessageRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var message = await dispatcher.SendAsync(new SendMessageCommand(tenantId, conversationId, request.ClientId, request.Text), cancellationToken);
        return Results.Created($"/api/v1/tenants/{tenantId}/messaging/conversations/{conversationId}/messages", message);
    }
}

/// <summary>§5.3: <c>clientId</c> lo genera la pantalla por intento de envío; un reintento con el mismo no duplica.</summary>
public sealed record SendMessageRequest(Guid? ClientId, string? Text);
