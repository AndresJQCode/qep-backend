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
        MapTasks(group);
        MapTicketTypes(group);
        MapSettings(group);
        MapConversations(group);
        MapThread(group);
        MapReplies(group);
        MapNotes(group);

        return endpoints;
    }

    // Spec 2026-11-15 §5.4: internal notes. Adding one takes no If-Match and does not change the ticket's version nor its ETag.
    private static void MapNotes(RouteGroupBuilder group)
    {
        group.MapGet("/tickets/{ticketId:guid}/notes", ListNotesAsync)
            .RequireAuthorization(SupportPolicies.TicketScopedView)
            .Produces<SupportTicketNotesDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/tickets/{ticketId:guid}/notes", AddNoteAsync)
            .RequireAuthorization(SupportPolicies.TicketAct)
            .Accepts<AddTicketNoteRequest>("application/json")
            .Produces<SupportTicketNoteDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Plan N18 and N19: Storage's member-less upload with the owner of (ticket, caller). The file does not pass through here: the
        // browser uploads it straight to the bucket with the signed URL.
        group.MapPost("/tickets/{ticketId:guid}/notes/attachments", OpenNoteAttachmentAsync)
            .RequireAuthorization(SupportPolicies.TicketAct)
            .Accepts<SupportNoteAttachmentRequest>("application/json")
            .Produces<SupportNoteAttachmentSessionDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/tickets/{ticketId:guid}/notes/attachments/{fileId:guid}/complete", CompleteNoteAttachmentAsync)
            .RequireAuthorization(SupportPolicies.TicketAct)
            .Produces<SupportNoteAttachmentStatusDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        // Plan N21: with seeing the ticket it is enough, like a public form attachment; it does not ask for storage.file.read.
        group.MapGet("/tickets/{ticketId:guid}/notes/{noteId:guid}/files/{fileId:guid}/download", DownloadNoteFileAsync)
            .RequireAuthorization(SupportPolicies.TicketScopedView)
            .Produces<SupportAttachmentDownloadDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> OpenNoteAttachmentAsync(
        Guid tenantId,
        Guid ticketId,
        SupportNoteAttachmentRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var session = await dispatcher.SendAsync(OpenNoteAttachmentCommand.From(tenantId, ticketId, request), cancellationToken);
        return Results.Created($"/api/v1/tenants/{tenantId}/support/tickets/{ticketId}/notes/attachments/{session.FileId}", session);
    }

    private static async Task<IResult> CompleteNoteAttachmentAsync(
        Guid tenantId, Guid ticketId, Guid fileId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new CompleteNoteAttachmentCommand(tenantId, ticketId, fileId), cancellationToken));

    private static async Task<IResult> DownloadNoteFileAsync(
        Guid tenantId, Guid ticketId, Guid noteId, Guid fileId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new GetNoteFileDownloadCommand(tenantId, ticketId, noteId, fileId), cancellationToken));

    private static async Task<IResult> ListNotesAsync(
        Guid tenantId,
        Guid ticketId,
        string? before,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        int take = ListTicketNotesQuery.DefaultTake) =>
        Results.Ok(await dispatcher.QueryAsync(new ListTicketNotesQuery(tenantId, ticketId, before, take), cancellationToken));

    private static async Task<IResult> AddNoteAsync(
        Guid tenantId, Guid ticketId, AddTicketNoteRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var note = await dispatcher.SendAsync(AddTicketNoteCommand.From(tenantId, ticketId, request), cancellationToken);
        return Results.Created($"/api/v1/tenants/{tenantId}/support/tickets/{ticketId}/notes", note);
    }

    // Fase 2: responder desde el ticket. Una respuesta que Meta o el correo rechazan también es 201, con la fila en Failed (plan E17).
    private static void MapReplies(RouteGroupBuilder group)
    {
        group.MapGet("/tickets/{ticketId:guid}/reply-options", GetReplyOptionsAsync)
            .RequireAuthorization(SupportPolicies.TicketView)
            .Produces<SupportReplyOptionsDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/tickets/{ticketId:guid}/replies", SendReplyAsync)
            .RequireAuthorization(SupportPermissions.Manage)
            .Accepts<SupportReplyRequest>("application/json")
            .Produces<SupportReplyResultDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);
    }

    private static async Task<IResult> GetReplyOptionsAsync(
        Guid tenantId, Guid ticketId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetReplyOptionsQuery(tenantId, ticketId), cancellationToken));

    private static async Task<IResult> SendReplyAsync(
        Guid tenantId,
        Guid ticketId,
        SupportReplyRequest request,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.SendAsync(
            SendTicketReplyCommand.From(tenantId, ticketId, request, RequireVersion(httpContext, "ticket")), cancellationToken);
        return Results.Created($"/api/v1/tenants/{tenantId}/support/tickets/{ticketId}", result);
    }

    // Fase 2: el expediente del ticket, «Seguir el hilo» y «¿De qué ticket es?». Fijar, soltar y seguir llevan If-Match (428/412,
    // como las acciones de la fase 1); asignar lleva la versión del ticket elegido en el cuerpo porque la ruta es de la conversación.
    private static void MapThread(RouteGroupBuilder group)
    {
        group.MapGet("/tickets/{ticketId:guid}/messages", ListTicketMessagesAsync)
            .RequireAuthorization(SupportPolicies.TicketView)
            .Produces<SupportTicketMessagesDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        MapTicketPost(group, "messages/{messageId:guid}/pin", PinMessageAsync);
        MapTicketPost(group, "messages/{messageId:guid}/release", ReleaseMessageAsync);
        MapTicketAction<SetTicketFollowThreadRequest>(group, "follow-thread", SetFollowThreadAsync);

        group.MapGet("/conversations/{conversationId:guid}/unclassified", ListUnclassifiedAsync)
            .RequireAuthorization(SupportPolicies.TicketView)
            .Produces<SupportUnclassifiedDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/conversations/{conversationId:guid}/unclassified/{messageId:guid}/assign", AssignUnclassifiedAsync)
            .RequireAuthorization(SupportPermissions.Manage)
            .Accepts<AssignUnclassifiedRequest>("application/json")
            .Produces<SupportUnclassifiedDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/conversations/{conversationId:guid}/unclassified/{messageId:guid}/dismiss", DismissUnclassifiedAsync)
            .RequireAuthorization(SupportPermissions.Manage)
            .Produces<SupportUnclassifiedDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    // La política por defecto es manage: lo que sólo tiene sentido para un externo. Las acciones que una tarea también admite pasan
    // TicketAct y el servicio de acceso decide por ticket (spec 2026-11-22 §5.6).
    private static RouteHandlerBuilder MapTicketPost(
        RouteGroupBuilder group, string action, Delegate handler, string policy = SupportPermissions.Manage) =>
        group.MapPost($"/tickets/{{ticketId:guid}}/{action}", handler)
            .RequireAuthorization(policy)
            .Produces<SupportTicketDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

    private static async Task<IResult> ListTicketMessagesAsync(
        Guid tenantId,
        Guid ticketId,
        string? before,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        int take = ListTicketMessagesQuery.DefaultTake) =>
        Results.Ok(await dispatcher.QueryAsync(new ListTicketMessagesQuery(tenantId, ticketId, before, take), cancellationToken));

    private static async Task<IResult> PinMessageAsync(
        Guid tenantId,
        Guid ticketId,
        Guid messageId,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new PinTicketMessageCommand(tenantId, ticketId, messageId, RequireVersion(httpContext, "ticket")), cancellationToken));

    private static async Task<IResult> ReleaseMessageAsync(
        Guid tenantId,
        Guid ticketId,
        Guid messageId,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new ReleaseTicketMessageCommand(tenantId, ticketId, messageId, RequireVersion(httpContext, "ticket")), cancellationToken));

    private static async Task<IResult> SetFollowThreadAsync(
        Guid tenantId,
        Guid ticketId,
        SetTicketFollowThreadRequest request,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            SetTicketFollowThreadCommand.From(tenantId, ticketId, request, RequireVersion(httpContext, "ticket")), cancellationToken));

    private static async Task<IResult> ListUnclassifiedAsync(
        Guid tenantId, Guid conversationId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListUnclassifiedQuery(tenantId, conversationId), cancellationToken));

    private static async Task<IResult> AssignUnclassifiedAsync(
        Guid tenantId,
        Guid conversationId,
        Guid messageId,
        AssignUnclassifiedRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            AssignUnclassifiedCommand.From(tenantId, conversationId, messageId, request), cancellationToken));

    private static async Task<IResult> DismissUnclassifiedAsync(
        Guid tenantId, Guid conversationId, Guid messageId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new DismissUnclassifiedCommand(tenantId, conversationId, messageId), cancellationToken));

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
        // Fase 4b (H24, Q-3, Q-16): el reporte, dentro de Soporte; lo ve quien tenga leer o gestionar.
        group.MapGet("/reports/summary", GetReportAsync)
            .RequireAuthorization(SupportPolicies.TicketView)
            .Produces<SupportReportDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/tickets", ListTicketsAsync)
            .RequireAuthorization(SupportPolicies.TicketScopedView)
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
            .RequireAuthorization(SupportPolicies.TicketScopedView)
            .Produces<SupportTicketDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Spec 2026-11-15 §7 (plan L20): what the requester was shown and accepted when filing. Seeing the ticket is enough.
        group.MapGet("/tickets/{ticketId:guid}/legal-acceptances/{kind}", GetLegalAcceptanceSnapshotAsync)
            .RequireAuthorization(SupportPolicies.TicketView)
            .Produces<SupportLegalAcceptanceSnapshotDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Fase 4b (H13): la URL firmada del PDF de una respuesta formal, con ver el ticket.
        group.MapGet("/tickets/{ticketId:guid}/replies/{replyId:guid}/document", DownloadReplyDocumentAsync)
            .RequireAuthorization(SupportPolicies.TicketView)
            .Produces<SupportDocumentLinkDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Fase 3 (plan F37): la URL firmada de un adjunto del formulario público. Con ver el ticket alcanza: no pide storage.file.read.
        group.MapGet("/tickets/{ticketId:guid}/attachments/{attachmentId:guid}/download", DownloadAttachmentAsync)
            .RequireAuthorization(SupportPolicies.TicketView)
            .Produces<SupportAttachmentDownloadDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        MapTicketAction<ChangeTicketStatusRequest>(group, "status", ChangeStatusAsync, SupportPolicies.TicketAct);
        MapTicketAction<AssignTicketRequest>(group, "assignee", AssignAsync, SupportPolicies.TicketAct);
        // Fase 4a (plan G7): «Asignármelo», sin cuerpo; el responsable sale de la sesión.
        MapTicketPost(group, "take", TakeAsync, SupportPolicies.TicketAct);
        // Fase 4a (plan G9): la prórroga única del plazo.
        MapTicketPost(group, "extension", ExtendAsync).Accepts<ExtendTicketRequest>("application/json");
        // Spec 2026-11-16: «Generar PIN nuevo» de un anónimo, sin cuerpo. El 200 trae issuedPin y consultUrl una sola vez.
        MapTicketPost(group, "pin", ReissuePinAsync);
        MapTicketAction<ChangeTicketTypeRequest>(group, "type", ChangeTypeAsync);
        MapTicketAction<LinkTicketCustomerRequest>(group, "customer", LinkCustomerAsync);
        MapTicketAction<ChangeTicketPriorityRequest>(group, "priority", ChangePriorityAsync, SupportPolicies.TicketAct);
        // Spec 2026-11-22: la fecha límite libre de una tarea. La puerta decide quién; sobre un externo es 422 not_internal.
        MapTicketAction<ChangeTicketDueDateRequest>(group, "due-date", ChangeDueDateAsync, SupportPolicies.TicketAct);

        group.MapGet("/assignees", ListAssigneesAsync)
            .RequireAuthorization(SupportPolicies.TicketView)
            .Produces<SupportAssigneesDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);
    }

    // Spec 2026-11-22 §5.2: las tareas internas entre miembros. La política deja pasar a quien gestiona o usa tareas; leer sola no crea.
    private static void MapTasks(RouteGroupBuilder group)
    {
        group.MapPost("/tasks", CreateTaskAsync)
            .RequireAuthorization(SupportPolicies.TicketAct)
            .Accepts<CreateTaskRequest>("application/json")
            .Produces<SupportTicketDetailDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/tasks/assignees", ListTaskAssigneesAsync)
            .RequireAuthorization(SupportPolicies.TicketAct)
            .Produces<SupportAssigneesDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);
    }

    private static async Task<IResult> CreateTaskAsync(
        Guid tenantId, CreateTaskRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var ticket = await dispatcher.SendAsync(CreateTaskCommand.From(tenantId, request), cancellationToken);
        return Results.Created($"/api/v1/tenants/{tenantId}/support/tickets/{ticket.Id}", ticket);
    }

    private static async Task<IResult> ListTaskAssigneesAsync(
        Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListTaskAssigneesQuery(tenantId), cancellationToken));

    private static void MapTicketAction<TRequest>(
        RouteGroupBuilder group, string action, Delegate handler, string policy = SupportPermissions.Manage)
        where TRequest : notnull =>
        group.MapPut($"/tickets/{{ticketId:guid}}/{action}", handler)
            .RequireAuthorization(policy)
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

        // Fase 4a (plan G4): sólo un tipo sin tickets; uno usado responde 422 support.ticket_type.in_use y se desactiva con el PUT.
        group.MapDelete("/settings/ticket-types/{typeId:guid}", DeleteTicketTypeAsync)
            .RequireAuthorization(SupportPermissions.SettingsManage)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
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

        // Fase 3 (plan F14 a F16): el formulario público y los documentos legales son versionados e insert-only. El If-Match es el número
        // de la versión vigente, y 0 vale: es la de un tenant que todavía no guardó nada.
        group.MapGet("/settings/public-form", GetFormSettingsAsync)
            .RequireAuthorization(SupportPolicies.SettingsView)
            .Produces<SupportFormSettingsDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPut("/settings/public-form", UpdateFormSettingsAsync)
            .RequireAuthorization(SupportPermissions.SettingsManage)
            .Accepts<UpdateFormSettingsRequest>("application/json")
            .Produces<SupportFormSettingsDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        // Spec 2026-11-15 §7: the three legal documents of the portal, each versioned on its own. {kind} is privacy-notice,
        // anonymous-notice or terms; the handler answers 404 for any other. The phase 3 legal-texts routes are gone (no consumer).
        group.MapGet("/settings/legal-documents", GetLegalDocumentsAsync)
            .RequireAuthorization(SupportPolicies.SettingsView)
            .Produces<SupportLegalDocumentsDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/settings/legal-documents/{kind}/versions", GetLegalDocumentHistoryAsync)
            .RequireAuthorization(SupportPolicies.SettingsView)
            .Produces<SupportLegalDocumentHistoryDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/settings/legal-documents/{kind}/versions/{version:int}", GetLegalDocumentVersionAsync)
            .RequireAuthorization(SupportPolicies.SettingsView)
            .Produces<SupportLegalDocumentDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/settings/legal-documents/{kind}", PublishLegalDocumentAsync)
            .RequireAuthorization(SupportPermissions.SettingsManage)
            .Accepts<PublishLegalDocumentRequest>("application/json")
            .Produces<SupportLegalDocumentDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);
    }

    private static async Task<IResult> GetFormSettingsAsync(
        Guid tenantId, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var form = await dispatcher.QueryAsync(new GetFormSettingsQuery(tenantId), cancellationToken);
        SetETag(httpContext, form.Version);
        return Results.Ok(form);
    }

    private static async Task<IResult> UpdateFormSettingsAsync(
        Guid tenantId,
        UpdateFormSettingsRequest request,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var expectedVersion = RequireVersion(httpContext, "public form", allowZero: true);
        var form = await dispatcher.SendAsync(UpdateFormSettingsCommand.From(tenantId, request, expectedVersion), cancellationToken);
        SetETag(httpContext, form.Version);
        return Results.Ok(form);
    }

    private static async Task<IResult> GetLegalDocumentsAsync(
        Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetLegalDocumentsQuery(tenantId), cancellationToken));

    private static async Task<IResult> GetLegalDocumentHistoryAsync(
        Guid tenantId, string kind, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetLegalDocumentHistoryQuery(tenantId, kind), cancellationToken));

    private static async Task<IResult> GetLegalDocumentVersionAsync(
        Guid tenantId, string kind, int version, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetLegalDocumentVersionQuery(tenantId, kind, version), cancellationToken));

    private static async Task<IResult> PublishLegalDocumentAsync(
        Guid tenantId,
        string kind,
        PublishLegalDocumentRequest request,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var expectedVersion = RequireVersion(httpContext, "legal document", allowZero: true);
        var document = await dispatcher.SendAsync(
            PublishLegalDocumentCommand.From(tenantId, kind, request, expectedVersion), cancellationToken);
        SetETag(httpContext, document.Version);
        return Results.Created(
            $"/api/v1/tenants/{tenantId}/support/settings/legal-documents/{kind}/versions/{document.Version}", document);
    }

    private static void SetETag(HttpContext httpContext, long version) =>
        httpContext.Response.Headers.ETag = $"\"{version}\"";

    private static async Task<IResult> ListTicketsAsync(
        Guid tenantId,
        string? status,
        Guid? typeId,
        string? assignee,
        string? due,
        string? search,
        string? priority,
        string? sort,
        string? kind,
        string? createdBy,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        int page = 1,
        int pageSize = ListTicketsQuery.DefaultPageSize) =>
        Results.Ok(await dispatcher.QueryAsync(
            new ListTicketsQuery(tenantId, status, typeId, assignee, due, search, priority, sort, page, pageSize, kind, createdBy),
            cancellationToken));

    // Las fechas llegan como texto: una mal escrita es 422 en su campo (el validador), no un 400 del binder.
    private static async Task<IResult> GetReportAsync(
        Guid tenantId, string? from, string? to, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetSupportReportQuery(tenantId, from, to), cancellationToken));

    private static async Task<IResult> CreateTicketAsync(
        Guid tenantId,
        CreateTicketRequest request,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var ticket = await dispatcher.SendAsync(CreateTicketCommand.From(tenantId, request), cancellationToken);
        NoStoreWhenPinIssued(httpContext, ticket);
        return Results.Created($"/api/v1/tenants/{tenantId}/support/tickets/{ticket.Id}", ticket);
    }

    private static async Task<IResult> GetTicketAsync(
        Guid tenantId, Guid ticketId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetTicketQuery(tenantId, ticketId), cancellationToken));

    private static async Task<IResult> GetLegalAcceptanceSnapshotAsync(
        Guid tenantId, Guid ticketId, string kind, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetLegalAcceptanceSnapshotQuery(tenantId, ticketId, kind), cancellationToken));

    private static async Task<IResult> DownloadAttachmentAsync(
        Guid tenantId, Guid ticketId, Guid attachmentId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new GetTicketAttachmentDownloadCommand(tenantId, ticketId, attachmentId), cancellationToken));

    private static async Task<IResult> DownloadReplyDocumentAsync(
        Guid tenantId, Guid ticketId, Guid replyId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new GetReplyDocumentDownloadCommand(tenantId, ticketId, replyId), cancellationToken));

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

    private static async Task<IResult> TakeAsync(
        Guid tenantId, Guid ticketId, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new TakeTicketCommand(tenantId, ticketId, RequireVersion(httpContext, "ticket")), cancellationToken));

    private static async Task<IResult> ReissuePinAsync(
        Guid tenantId, Guid ticketId, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var ticket = await dispatcher.SendAsync(
            new ReissueTicketPinCommand(tenantId, ticketId, RequireVersion(httpContext, "ticket")), cancellationToken);
        NoStoreWhenPinIssued(httpContext, ticket);
        return Results.Ok(ticket);
    }

    // Una respuesta con el PIN en claro no debe quedar en ninguna caché (navegador, proxy, CDN): el PIN se muestra una sola vez.
    private static void NoStoreWhenPinIssued(HttpContext httpContext, SupportTicketDetailDto ticket)
    {
        if (ticket.IssuedPin is not null)
        {
            httpContext.Response.Headers.CacheControl = "no-store";
        }
    }

    private static async Task<IResult> ExtendAsync(
        Guid tenantId,
        Guid ticketId,
        ExtendTicketRequest request,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            ExtendTicketCommand.From(tenantId, ticketId, request, RequireVersion(httpContext, "ticket")), cancellationToken));

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

    private static async Task<IResult> ChangePriorityAsync(
        Guid tenantId,
        Guid ticketId,
        ChangeTicketPriorityRequest request,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            ChangeTicketPriorityCommand.From(tenantId, ticketId, request, RequireVersion(httpContext, "ticket")), cancellationToken));

    private static async Task<IResult> ChangeDueDateAsync(
        Guid tenantId,
        Guid ticketId,
        ChangeTicketDueDateRequest request,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            ChangeTicketDueDateCommand.From(tenantId, ticketId, request, RequireVersion(httpContext, "ticket")), cancellationToken));

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

    private static async Task<IResult> DeleteTicketTypeAsync(
        Guid tenantId, Guid typeId, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(
            new DeleteTicketTypeCommand(tenantId, typeId, RequireVersion(httpContext, "ticket type")), cancellationToken);
        return Results.NoContent();
    }

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
    // handler. allowZero: el recurso versionado insert-only (formulario, textos legales), cuya versión 0 es «todavía no hay ninguna».
    private static long RequireVersion(HttpContext httpContext, string resource, bool allowZero = false) =>
        TryParseVersion(httpContext.Request.Headers.IfMatch, out var version) && (allowZero || version > 0)
            ? version
            : throw new PreconditionRequiredException(
                "precondition.if_match_required",
                $"A valid If-Match header containing the loaded {resource} version is required.");

    // Cualquier entero no negativo; RequireVersion decide si el 0 vale.
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
        return long.TryParse(normalized, out version) && version >= 0;
    }
}
