using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Support.Application;

namespace Modules.Support.Api;

public static class SupportEndpoints
{
    public static IEndpointRouteBuilder MapSupportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Tenant en la ruta, como loans: la política frena a quien no tiene el permiso y el handler
        // (SupportAuthorization) a quien lo tiene en otro tenant.
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/support")
            .WithTags("Support");

        MapTickets(group);
        MapTicketTypes(group);
        MapSettings(group);
        MapConversations(group);

        return endpoints;
    }

    // Fase 2 (Mensajería): lo que parte de una conversación. La conversación de otro tenant es 404, como un ticket ajeno.
    private static void MapConversations(RouteGroupBuilder group)
    {
        group.MapGet("/conversations/{conversationId:guid}/tickets", ListConversationTicketsAsync)
            .RequireAuthorization(SupportPolicies.TicketView)
            .Produces<SupportConversationTicketsDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/conversations/{conversationId:guid}/tickets", CreateTicketFromConversationAsync)
            .RequireAuthorization(SupportPermissions.Manage)
            .Accepts<CreateTicketFromConversationRequest>("application/json")
            .Produces<SupportTicketDetailDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    private static async Task<IResult> ListConversationTicketsAsync(
        Guid tenantId, Guid conversationId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListConversationTicketsQuery(tenantId, conversationId), cancellationToken));

    private static async Task<IResult> CreateTicketFromConversationAsync(
        Guid tenantId,
        Guid conversationId,
        CreateTicketFromConversationRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var ticket = await dispatcher.SendAsync(
            CreateTicketFromConversationCommand.From(tenantId, conversationId, request), cancellationToken);
        return Results.Created($"/api/v1/tenants/{tenantId}/support/tickets/{ticket.Id}", ticket);
    }

    private static void MapTickets(RouteGroupBuilder group)
    {
        group.MapGet("/tickets", ListTicketsAsync)
            .RequireAuthorization(SupportPolicies.TicketView)
            .Produces<SupportTicketPageDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/tickets", CreateTicketAsync)
            .RequireAuthorization(SupportPermissions.Manage)
            .Accepts<CreateTicketRequest>("application/json")
            .Produces<SupportTicketDetailDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/tickets/{ticketId:guid}", GetTicketAsync)
            .RequireAuthorization(SupportPolicies.TicketView)
            .Produces<SupportTicketDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        MapTicketAction<ChangeTicketStatusRequest>(group, "status", ChangeStatusAsync);
        MapTicketAction<AssignTicketRequest>(group, "assignee", AssignAsync);
        MapTicketAction<ChangeTicketTypeRequest>(group, "type", ChangeTypeAsync);
        MapTicketAction<LinkTicketCustomerRequest>(group, "customer", LinkCustomerAsync);

        group.MapGet("/assignees", ListAssigneesAsync)
            .RequireAuthorization(SupportPolicies.TicketView)
            .Produces<SupportAssigneesDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);
    }

    private static void MapTicketAction<TRequest>(RouteGroupBuilder group, string action, Delegate handler)
        where TRequest : notnull =>
        group.MapPut($"/tickets/{{ticketId:guid}}/{action}", handler)
            .RequireAuthorization(SupportPermissions.Manage)
            .Accepts<TRequest>("application/json")
            .Produces<SupportTicketDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

    private static void MapTicketTypes(RouteGroupBuilder group)
    {
        group.MapGet("/ticket-types", ListTicketTypesAsync)
            .RequireAuthorization(SupportPolicies.TicketView)
            .Produces<IReadOnlyList<SupportTicketTypeDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/settings/ticket-types", ListSettingsTicketTypesAsync)
            .RequireAuthorization(SupportPermissions.SettingsManage)
            .Produces<IReadOnlyList<SupportTicketTypeDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/settings/ticket-types", CreateTicketTypeAsync)
            .RequireAuthorization(SupportPermissions.SettingsManage)
            .Accepts<TicketTypeRequest>("application/json")
            .Produces<SupportTicketTypeDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/settings/ticket-types/order", ReorderTicketTypesAsync)
            .RequireAuthorization(SupportPermissions.SettingsManage)
            .Accepts<ReorderTicketTypesRequest>("application/json")
            .Produces<IReadOnlyList<SupportTicketTypeDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/settings/ticket-types/{typeId:guid}", UpdateTicketTypeAsync)
            .RequireAuthorization(SupportPermissions.SettingsManage)
            .Accepts<TicketTypeRequest>("application/json")
            .Produces<SupportTicketTypeDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);
    }

    private static void MapSettings(RouteGroupBuilder group)
    {
        group.MapGet("/settings", GetSettingsAsync)
            .RequireAuthorization(SupportPolicies.SettingsView)
            .Produces<SupportSettingsDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPut("/settings", UpdateSettingsAsync)
            .RequireAuthorization(SupportPermissions.SettingsManage)
            .Accepts<UpdateSupportSettingsRequest>("application/json")
            .Produces<SupportSettingsDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);
    }

    private static async Task<IResult> ListTicketsAsync(
        Guid tenantId,
        string? status,
        Guid? typeId,
        string? assignee,
        string? due,
        string? search,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        int page = 1,
        int pageSize = ListTicketsQuery.DefaultPageSize) =>
        Results.Ok(await dispatcher.QueryAsync(
            new ListTicketsQuery(tenantId, status, typeId, assignee, due, search, page, pageSize), cancellationToken));

    private static async Task<IResult> CreateTicketAsync(
        Guid tenantId, CreateTicketRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var ticket = await dispatcher.SendAsync(CreateTicketCommand.From(tenantId, request), cancellationToken);
        return Results.Created($"/api/v1/tenants/{tenantId}/support/tickets/{ticket.Id}", ticket);
    }

    private static async Task<IResult> GetTicketAsync(
        Guid tenantId, Guid ticketId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetTicketQuery(tenantId, ticketId), cancellationToken));

    private static async Task<IResult> ChangeStatusAsync(
        Guid tenantId,
        Guid ticketId,
        ChangeTicketStatusRequest request,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            ChangeTicketStatusCommand.From(tenantId, ticketId, request, RequireVersion(httpContext, "ticket")), cancellationToken));

    private static async Task<IResult> AssignAsync(
        Guid tenantId,
        Guid ticketId,
        AssignTicketRequest request,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            AssignTicketCommand.From(tenantId, ticketId, request, RequireVersion(httpContext, "ticket")), cancellationToken));

    private static async Task<IResult> ChangeTypeAsync(
        Guid tenantId,
        Guid ticketId,
        ChangeTicketTypeRequest request,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            ChangeTicketTypeCommand.From(tenantId, ticketId, request, RequireVersion(httpContext, "ticket")), cancellationToken));

    private static async Task<IResult> LinkCustomerAsync(
        Guid tenantId,
        Guid ticketId,
        LinkTicketCustomerRequest request,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            LinkTicketCustomerCommand.From(tenantId, ticketId, request, RequireVersion(httpContext, "ticket")), cancellationToken));

    private static async Task<IResult> ListAssigneesAsync(
        Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListSupportAssigneesQuery(tenantId), cancellationToken));

    private static async Task<IResult> ListTicketTypesAsync(
        Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListTicketTypesQuery(tenantId, ForSettings: false), cancellationToken));

    private static async Task<IResult> ListSettingsTicketTypesAsync(
        Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListTicketTypesQuery(tenantId, ForSettings: true), cancellationToken));

    private static async Task<IResult> CreateTicketTypeAsync(
        Guid tenantId, TicketTypeRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var type = await dispatcher.SendAsync(CreateTicketTypeCommand.From(tenantId, request), cancellationToken);
        return Results.Created($"/api/v1/tenants/{tenantId}/support/settings/ticket-types/{type.Id}", type);
    }

    private static async Task<IResult> UpdateTicketTypeAsync(
        Guid tenantId,
        Guid typeId,
        TicketTypeRequest request,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            UpdateTicketTypeCommand.From(tenantId, typeId, request, RequireVersion(httpContext, "ticket type")), cancellationToken));

    private static async Task<IResult> ReorderTicketTypesAsync(
        Guid tenantId, ReorderTicketTypesRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(ReorderTicketTypesCommand.From(tenantId, request), cancellationToken));

    private static async Task<IResult> GetSettingsAsync(
        Guid tenantId, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        WithETag(httpContext, await dispatcher.QueryAsync(new GetSupportSettingsQuery(tenantId), cancellationToken));

    // Mismo contrato que StoreEndpoints: sin If-Match 428, versión vieja 412 (en el handler), y el ETag trae la nueva.
    private static async Task<IResult> UpdateSettingsAsync(
        Guid tenantId,
        UpdateSupportSettingsRequest request,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        WithETag(
            httpContext,
            await dispatcher.SendAsync(
                UpdateSupportSettingsCommand.From(tenantId, request, RequireVersion(httpContext, "support settings")), cancellationToken));

    private static IResult WithETag(HttpContext httpContext, SupportSettingsDto settings)
    {
        httpContext.Response.Headers.ETag = $"\"{settings.Version}\"";
        return Results.Ok(settings);
    }

    // Copia de LoansEndpoints.RequireVersion (Support no referencia ese módulo): sin If-Match 428; la versión vieja es 412 en el
    // handler.
    private static long RequireVersion(HttpContext httpContext, string resource) =>
        TryParseVersion(httpContext.Request.Headers.IfMatch, out var version)
            ? version
            : throw new PreconditionRequiredException(
                "precondition.if_match_required",
                $"A valid If-Match header containing the loaded {resource} version is required.");

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
