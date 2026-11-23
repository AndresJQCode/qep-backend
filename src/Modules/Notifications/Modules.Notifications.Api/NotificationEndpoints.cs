using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Notifications.Application;

namespace Modules.Notifications.Api;

/// <summary>
/// Spec 2026-11-22 §8: the member's bell. Session only, no permission: each member reads what is theirs, and every handler revalidates
/// tenant and membership (403, never 404, for a tenant that is not the caller's).
/// </summary>
public static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/notifications")
            .WithTags("Notifications")
            .RequireAuthorization();

        group.MapGet("/", ListAsync)
            .Produces<InAppNotificationPageDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/unread-count", UnreadCountAsync)
            .Produces<UnreadCountDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/{notificationId:guid}/read", MarkReadAsync)
            .Produces<InAppNotificationReadDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/read-all", MarkAllReadAsync)
            .Produces<MarkAllReadResultDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/read-by-resource", MarkReadByResourceAsync)
            .Produces<MarkResourceReadResultDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        Guid tenantId,
        string? cursor,
        bool? unreadOnly,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        int limit = ListInAppNotificationsQuery.DefaultLimit) =>
        Results.Ok(await dispatcher.QueryAsync(
            new ListInAppNotificationsQuery(tenantId, cursor, limit, unreadOnly ?? false), cancellationToken));

    private static async Task<IResult> UnreadCountAsync(Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetUnreadCountQuery(tenantId), cancellationToken));

    private static async Task<IResult> MarkReadAsync(
        Guid tenantId, Guid notificationId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new MarkNotificationReadCommand(tenantId, notificationId), cancellationToken));

    // Task-notices spec D-2/D-3: opening a ticket reads its assignment entries. A missing body is the same 422 as an empty one.
    private static async Task<IResult> MarkReadByResourceAsync(
        Guid tenantId, MarkResourceReadRequest? request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new MarkResourceNotificationsReadCommand(tenantId, request?.Kind ?? string.Empty, request?.ResourceId ?? Guid.Empty),
            cancellationToken));

    // The body is optional: without one (or with a null upTo) every unread entry is marked.
    private static async Task<IResult> MarkAllReadAsync(
        Guid tenantId, MarkAllReadRequest? request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new MarkAllNotificationsReadCommand(tenantId, request?.UpTo), cancellationToken));
}
