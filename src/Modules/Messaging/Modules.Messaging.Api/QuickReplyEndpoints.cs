using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Messaging.Application;

namespace Modules.Messaging.Api;

/// <summary>
/// Spec 2026-10-12 §3.6: las respuestas rápidas, bajo <c>/api/v1/tenants/{tenantId:guid}/messaging/quick-replies</c>. Leer con
/// <c>messaging.conversation.read</c> (todo asesor de la caja); gestionar con <c>messaging.quick_reply.manage</c>. Los handlers
/// revalidan tenant, permiso y módulo (403 con código, nunca 404 ante otro tenant).
/// </summary>
internal static class QuickReplyEndpoints
{
    public static void MapQuickReplyEndpoints(this RouteGroupBuilder messaging)
    {
        ArgumentNullException.ThrowIfNull(messaging);
        var group = messaging.MapGroup("/quick-replies");

        group.MapGet("/", ListAsync)
            .RequireAuthorization(MessagingPermissions.ConversationRead)
            .Produces<QuickReplyListDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/", CreateAsync)
            .RequireAuthorization(MessagingPermissions.QuickReplyManage)
            .Accepts<SaveQuickReplyRequest>("application/json")
            .Produces<QuickReplyDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/{quickReplyId:guid}", UpdateAsync)
            .RequireAuthorization(MessagingPermissions.QuickReplyManage)
            .Accepts<SaveQuickReplyRequest>("application/json")
            .Produces<QuickReplyDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapDelete("/{quickReplyId:guid}", DeleteAsync)
            .RequireAuthorization(MessagingPermissions.QuickReplyManage)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    private static async Task<IResult> ListAsync(Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListQuickRepliesQuery(tenantId), cancellationToken));

    // Sin Location: no hay GET de una sola respuesta; la pantalla repinta la lista con lo que vuelve.
    private static async Task<IResult> CreateAsync(
        Guid tenantId, SaveQuickReplyRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Created(
            (string?)null,
            await dispatcher.SendAsync(new CreateQuickReplyCommand(tenantId, request.Shortcut, request.Body), cancellationToken));

    private static async Task<IResult> UpdateAsync(
        Guid tenantId, Guid quickReplyId, SaveQuickReplyRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new UpdateQuickReplyCommand(tenantId, quickReplyId, request.Shortcut, request.Body, request.Version), cancellationToken));

    private static async Task<IResult> DeleteAsync(
        Guid tenantId, Guid quickReplyId, long? version, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(new DeleteQuickReplyCommand(tenantId, quickReplyId, version), cancellationToken);
        return Results.NoContent();
    }
}

/// <summary>§3.6: el cuerpo de crear y editar. <c>shortcut</c> sin <c>/</c>. El <c>POST</c> ignora <c>version</c>; el <c>PUT</c> la
/// exige. Todo opcional para que lo que falte salga 422 con su clave en <c>errors</c>, no 400 del binder.</summary>
public sealed record SaveQuickReplyRequest(string? Shortcut, string? Body, long? Version);
