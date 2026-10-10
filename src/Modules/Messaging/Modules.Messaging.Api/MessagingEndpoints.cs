using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Messaging.Application;

namespace Modules.Messaging.Api;

public static class MessagingEndpoints
{
    /// <summary>Lista, detalle e hilo (Task 14), búsqueda (Task 15) y envío (Task 16), leído, resolver y reabrir (Task 17);
    /// el medio se suma en la siguiente, todos bajo <c>/api/v1/tenants/{tenantId:guid}/messaging</c>.</summary>
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

        // Spec 2026-10-09 §8.4: sin If-Match; el acuse a Meta es best effort y la respuesta siempre 204.
        group.MapPost("/conversations/{conversationId:guid}/read", MarkReadAsync)
            .RequireAuthorization(MessagingPermissions.ConversationManage)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Spec 2026-10-09 §8.5: por el agregado con If-Match (428 sin él, 412 con versión vieja).
        group.MapPost("/conversations/{conversationId:guid}/resolve", ResolveAsync)
            .RequireAuthorization(MessagingPermissions.ConversationManage)
            .Produces<ConversationSummary>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapPost("/conversations/{conversationId:guid}/reopen", ReopenAsync)
            .RequireAuthorization(MessagingPermissions.ConversationManage)
            .Produces<ConversationSummary>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

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
        // Sin Location: no hay GET de un mensaje suelto, y apuntar a la colección del hilo confundiría.
        return Results.Created((string?)null, message);
    }

    private static async Task<IResult> MarkReadAsync(Guid tenantId, Guid conversationId, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(new MarkConversationReadCommand(tenantId, conversationId), cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> ResolveAsync(Guid tenantId, Guid conversationId, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new ResolveConversationCommand(tenantId, conversationId, RequireVersion(httpContext)), cancellationToken));

    private static async Task<IResult> ReopenAsync(Guid tenantId, Guid conversationId, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new ReopenConversationCommand(tenantId, conversationId, RequireVersion(httpContext)), cancellationToken));

    // Copia de IntegrationsEndpoints: mismo contrato que /pos y /orders-export-layout; sin If-Match 428,
    // vieja 412 (en el handler).
    private static long RequireVersion(HttpContext httpContext) =>
        TryParseVersion(httpContext.Request.Headers.IfMatch, out var version)
            ? version
            : throw new PreconditionRequiredException(
                "precondition.if_match_required",
                "A valid If-Match header containing the loaded conversation version is required.");

    private static bool TryParseVersion(string? etag, out long version)
    {
        version = 0;
        if (string.IsNullOrWhiteSpace(etag))
        {
            return false;
        }

        var normalized = etag.Trim();
        if (normalized.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..].Trim();
        }

        normalized = normalized.Trim('"');
        return long.TryParse(normalized, out version) && version > 0;
    }
}

/// <summary>§5.3: <c>clientId</c> lo genera la pantalla por intento de envío; un reintento con el mismo no duplica.</summary>
public sealed record SendMessageRequest(Guid? ClientId, string? Text);
