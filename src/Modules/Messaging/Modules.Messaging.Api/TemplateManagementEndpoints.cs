using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Messaging.Application;

namespace Modules.Messaging.Api;

/// <summary>
/// Spec 2026-10-14 §4.7: la administración de plantillas de una conexión, bajo
/// <c>/api/v1/tenants/{tenantId:guid}/messaging/connections/{connectionId:guid}/template-management</c>, toda con
/// <c>messaging.template.manage</c>. Los handlers revalidan tenant, permiso, módulo y conexión (403 con código, nunca 404 ante otro tenant;
/// 422 <c>connection_unavailable</c> ante una conexión ajena).
/// </summary>
internal static class TemplateManagementEndpoints
{
    public static void MapTemplateManagementEndpoints(this RouteGroupBuilder messaging)
    {
        ArgumentNullException.ThrowIfNull(messaging);
        var group = messaging.MapGroup("/connections/{connectionId:guid}/template-management");

        group.MapGet("/", GetAsync)
            .RequireAuthorization(MessagingPermissions.TemplateManage)
            .Produces<TemplateManagementDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/sync", SyncAsync)
            .RequireAuthorization(MessagingPermissions.TemplateManage)
            .Produces<TemplateManagementDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/templates", CreateAsync)
            .RequireAuthorization(MessagingPermissions.TemplateManage)
            .Accepts<CreateTemplateRequest>("application/json")
            .Produces<ManagedTemplateDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Spec 2026-11-17: la sesión de subida de la imagen fija del encabezado. Sólo messaging.template.manage, nunca storage.file.*.
        group.MapPost("/header-image/upload-session", CreateHeaderImageSessionAsync)
            .RequireAuthorization(MessagingPermissions.TemplateManage)
            .Accepts<CreateTemplateHeaderImageSessionRequest>("application/json")
            .Produces<TemplateHeaderImageSessionDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // El cuerpo HTTP es opcional: sin él se crea el texto base de la definición (spec §4.5) y se adopta una externa compatible. Dejar
        // que se infiera requerido responde 500 cuando un frontend viejo no manda nada (precedente: el envío de cotizaciones).
        group.MapPost("/quotation", ProvisionQuotationAsync)
            .RequireAuthorization(MessagingPermissions.TemplateManage)
            .Accepts<ProvisionQuotationTemplateRequest>(isOptional: true, "application/json")
            .Produces<ManagedTemplateDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Spec 2026-11-09 D3: la plantilla de pedido, calcada de la de cotización (cuerpo opcional, sin gate de módulo).
        group.MapPost("/order", ProvisionOrderAsync)
            .RequireAuthorization(MessagingPermissions.TemplateManage)
            .Accepts<ProvisionOrderTemplateRequest>(isOptional: true, "application/json")
            .Produces<ManagedTemplateDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Spec 2026-11-21: la plantilla de factura, calcada de la de pedido (cuerpo opcional, sin gate de módulo).
        group.MapPost("/invoice", ProvisionInvoiceAsync)
            .RequireAuthorization(MessagingPermissions.TemplateManage)
            .Accepts<ProvisionInvoiceTemplateRequest>(isOptional: true, "application/json")
            .Produces<ManagedTemplateDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Soporte fase 2: la plantilla «Aviso de respuesta», calcada de la de pedido (cuerpo opcional, sin encabezado ni gate de módulo).
        group.MapPost("/support-reply", ProvisionSupportReplyAsync)
            .RequireAuthorization(MessagingPermissions.TemplateManage)
            .Accepts<ProvisionSupportReplyTemplateRequest>(isOptional: true, "application/json")
            .Produces<ManagedTemplateDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Soporte fase 4b: la plantilla «Respuesta formal», calcada de la de pedido (cuerpo opcional, encabezado de documento, sin gate de módulo).
        group.MapPost("/support-formal-reply", ProvisionSupportFormalReplyAsync)
            .RequireAuthorization(MessagingPermissions.TemplateManage)
            .Accepts<ProvisionSupportFormalReplyTemplateRequest>(isOptional: true, "application/json")
            .Produces<ManagedTemplateDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Soporte fase 4b: la plantilla de la encuesta (cuerpo opcional, botones fijos, sin gate de módulo).
        group.MapPost("/support-survey", ProvisionSupportSurveyAsync)
            .RequireAuthorization(MessagingPermissions.TemplateManage)
            .Accepts<ProvisionSupportSurveyTemplateRequest>(isOptional: true, "application/json")
            .Produces<ManagedTemplateDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Mayorista: la plantilla de aviso de invitación, calcada de la de pedido (cuerpo opcional, sin encabezado ni gate de módulo).
        group.MapPost("/wholesale-invitation-notice", ProvisionWholesaleInvitationNoticeAsync)
            .RequireAuthorization(MessagingPermissions.TemplateManage)
            .Accepts<ProvisionWholesaleInvitationNoticeTemplateRequest>(isOptional: true, "application/json")
            .Produces<ManagedTemplateDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Support task notices: the assignment notice template (optional body, body only, no module gate).
        group.MapPost("/task-notice", ProvisionTaskNoticeAsync)
            .RequireAuthorization(MessagingPermissions.TemplateManage)
            .Accepts<ProvisionTaskNoticeTemplateRequest>(isOptional: true, "application/json")
            .Produces<ManagedTemplateDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Agenda: la plantilla del recordatorio de cita, calcada de la del aviso de Mayorista (cuerpo opcional, sin encabezado ni gate de módulo).
        group.MapPost("/appointment-reminder", ProvisionAppointmentReminderAsync)
            .RequireAuthorization(MessagingPermissions.TemplateManage)
            .Accepts<ProvisionAppointmentReminderTemplateRequest>(isOptional: true, "application/json")
            .Produces<ManagedTemplateDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Cobros recurrentes §5: las dos plantillas de cobro, también con el cuerpo HTTP opcional (el texto base vive en el backend).
        group.MapPost("/billing", ProvisionBillingAsync)
            .RequireAuthorization(MessagingPermissions.TemplateManage)
            .Accepts<ProvisionBillingTemplatesRequest>(isOptional: true, "application/json")
            .Produces<IReadOnlyList<ManagedTemplateDto>>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Los borradores que abren la ventana de edición: el texto inicial y si se puede editar ya vienen resueltos (BFF).
        group.MapGet("/quotation/draft", GetQuotationDraftAsync)
            .RequireAuthorization(MessagingPermissions.TemplateManage)
            .Produces<SystemTemplateDraftDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/order/draft", GetOrderDraftAsync)
            .RequireAuthorization(MessagingPermissions.TemplateManage)
            .Produces<SystemTemplateDraftDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/invoice/draft", GetInvoiceDraftAsync)
            .RequireAuthorization(MessagingPermissions.TemplateManage)
            .Produces<SystemTemplateDraftDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/support-reply/draft", GetSupportReplyDraftAsync)
            .RequireAuthorization(MessagingPermissions.TemplateManage)
            .Produces<SystemTemplateDraftDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/support-formal-reply/draft", GetSupportFormalReplyDraftAsync)
            .RequireAuthorization(MessagingPermissions.TemplateManage)
            .Produces<SystemTemplateDraftDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/support-survey/draft", GetSupportSurveyDraftAsync)
            .RequireAuthorization(MessagingPermissions.TemplateManage)
            .Produces<SystemTemplateDraftDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/wholesale-invitation-notice/draft", GetWholesaleInvitationNoticeDraftAsync)
            .RequireAuthorization(MessagingPermissions.TemplateManage)
            .Produces<SystemTemplateDraftDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/task-notice/draft", GetTaskNoticeDraftAsync)
            .RequireAuthorization(MessagingPermissions.TemplateManage)
            .Produces<SystemTemplateDraftDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/appointment-reminder/draft", GetAppointmentReminderDraftAsync)
            .RequireAuthorization(MessagingPermissions.TemplateManage)
            .Produces<SystemTemplateDraftDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/billing/draft", GetBillingDraftAsync)
            .RequireAuthorization(MessagingPermissions.TemplateManage)
            .Produces<BillingTemplatesDraftDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Spec 2026-11-20: cambiar la imagen fija del encabezado de una plantilla guardada, sin pasar por Meta. Devuelve la plantilla entera,
        // que es lo que la pantalla repinta.
        group.MapPut("/templates/{templateId:guid}/header-image", ReplaceHeaderImageAsync)
            .RequireAuthorization(MessagingPermissions.TemplateManage)
            .Accepts<ReplaceTemplateHeaderImageRequest>("application/json")
            .Produces<ManagedTemplateDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapDelete("/templates/{templateId:guid}", DeleteAsync)
            .RequireAuthorization(MessagingPermissions.TemplateManage)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    private static async Task<IResult> GetAsync(Guid tenantId, Guid connectionId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetTemplateManagementQuery(tenantId, connectionId), cancellationToken));

    private static async Task<IResult> SyncAsync(Guid tenantId, Guid connectionId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new SyncTemplatesCommand(tenantId, connectionId), cancellationToken));

    // Location = la pantalla: no hay GET de una sola plantilla; el frontend repinta la lista con lo que vuelve.
    private static async Task<IResult> CreateAsync(
        Guid tenantId, Guid connectionId, CreateTemplateRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Created(
            $"/api/v1/tenants/{tenantId}/messaging/connections/{connectionId}/template-management",
            await dispatcher.SendAsync(
                new CreateTemplateCommand(
                    tenantId, connectionId, request.Name, request.Language, request.Category, request.Header, request.Body, request.Footer,
                    request.Buttons?.Select(button => new TemplateButtonInput(button.Type, button.Text, button.Url, button.PhoneNumber)).ToArray(),
                    request.Examples,
                    request.HeaderImageFileId),
                cancellationToken));

    private static async Task<IResult> CreateHeaderImageSessionAsync(
        Guid tenantId, Guid connectionId, CreateTemplateHeaderImageSessionRequest request, IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new CreateTemplateHeaderImageSessionCommand(tenantId, connectionId, request.FileName, request.MimeType, request.SizeBytes),
            cancellationToken));

    private static async Task<IResult> ReplaceHeaderImageAsync(
        Guid tenantId, Guid connectionId, Guid templateId, ReplaceTemplateHeaderImageRequest request, IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new ReplaceTemplateHeaderImageCommand(tenantId, connectionId, templateId, request.HeaderImageFileId), cancellationToken));

    // Location = la pantalla, como al crear una libre.
    private static async Task<IResult> ProvisionQuotationAsync(
        Guid tenantId, Guid connectionId, ProvisionQuotationTemplateRequest? request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Created(
            $"/api/v1/tenants/{tenantId}/messaging/connections/{connectionId}/template-management",
            await dispatcher.SendAsync(new ProvisionQuotationTemplateCommand(tenantId, connectionId, request?.Body), cancellationToken));

    // Location = la pantalla, como al provisionar la de cotización.
    private static async Task<IResult> ProvisionOrderAsync(
        Guid tenantId, Guid connectionId, ProvisionOrderTemplateRequest? request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Created(
            $"/api/v1/tenants/{tenantId}/messaging/connections/{connectionId}/template-management",
            await dispatcher.SendAsync(new ProvisionOrderTemplateCommand(tenantId, connectionId, request?.Body), cancellationToken));

    private static async Task<IResult> ProvisionInvoiceAsync(
        Guid tenantId, Guid connectionId, ProvisionInvoiceTemplateRequest? request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Created(
            $"/api/v1/tenants/{tenantId}/messaging/connections/{connectionId}/template-management",
            await dispatcher.SendAsync(new ProvisionInvoiceTemplateCommand(tenantId, connectionId, request?.Body), cancellationToken));

    private static async Task<IResult> ProvisionSupportReplyAsync(
        Guid tenantId, Guid connectionId, ProvisionSupportReplyTemplateRequest? request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Created(
            $"/api/v1/tenants/{tenantId}/messaging/connections/{connectionId}/template-management",
            await dispatcher.SendAsync(new ProvisionSupportReplyTemplateCommand(tenantId, connectionId, request?.Body), cancellationToken));

    private static async Task<IResult> ProvisionSupportFormalReplyAsync(
        Guid tenantId, Guid connectionId, ProvisionSupportFormalReplyTemplateRequest? request, IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Created(
            $"/api/v1/tenants/{tenantId}/messaging/connections/{connectionId}/template-management",
            await dispatcher.SendAsync(new ProvisionSupportFormalReplyTemplateCommand(tenantId, connectionId, request?.Body), cancellationToken));

    private static async Task<IResult> ProvisionSupportSurveyAsync(
        Guid tenantId, Guid connectionId, ProvisionSupportSurveyTemplateRequest? request, IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Created(
            $"/api/v1/tenants/{tenantId}/messaging/connections/{connectionId}/template-management",
            await dispatcher.SendAsync(new ProvisionSupportSurveyTemplateCommand(tenantId, connectionId, request?.Body), cancellationToken));

    private static async Task<IResult> ProvisionWholesaleInvitationNoticeAsync(
        Guid tenantId, Guid connectionId, ProvisionWholesaleInvitationNoticeTemplateRequest? request, IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Created(
            $"/api/v1/tenants/{tenantId}/messaging/connections/{connectionId}/template-management",
            await dispatcher.SendAsync(new ProvisionWholesaleInvitationNoticeTemplateCommand(tenantId, connectionId, request?.Body), cancellationToken));

    private static async Task<IResult> ProvisionTaskNoticeAsync(
        Guid tenantId, Guid connectionId, ProvisionTaskNoticeTemplateRequest? request, IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Created(
            $"/api/v1/tenants/{tenantId}/messaging/connections/{connectionId}/template-management",
            await dispatcher.SendAsync(new ProvisionTaskNoticeTemplateCommand(tenantId, connectionId, request?.Body), cancellationToken));

    private static async Task<IResult> ProvisionAppointmentReminderAsync(
        Guid tenantId, Guid connectionId, ProvisionAppointmentReminderTemplateRequest? request, IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Created(
            $"/api/v1/tenants/{tenantId}/messaging/connections/{connectionId}/template-management",
            await dispatcher.SendAsync(new ProvisionAppointmentReminderTemplateCommand(tenantId, connectionId, request?.Body), cancellationToken));

    private static async Task<IResult> ProvisionBillingAsync(
        Guid tenantId, Guid connectionId, ProvisionBillingTemplatesRequest? request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Created(
            $"/api/v1/tenants/{tenantId}/messaging/connections/{connectionId}/template-management",
            await dispatcher.SendAsync(new ProvisionBillingTemplatesCommand(tenantId, connectionId, request?.ReminderBody, request?.OverdueBody), cancellationToken));

    private static async Task<IResult> GetQuotationDraftAsync(Guid tenantId, Guid connectionId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetQuotationTemplateDraftQuery(tenantId, connectionId), cancellationToken));

    private static async Task<IResult> GetOrderDraftAsync(Guid tenantId, Guid connectionId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetOrderTemplateDraftQuery(tenantId, connectionId), cancellationToken));

    private static async Task<IResult> GetInvoiceDraftAsync(Guid tenantId, Guid connectionId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetInvoiceTemplateDraftQuery(tenantId, connectionId), cancellationToken));

    private static async Task<IResult> GetSupportReplyDraftAsync(Guid tenantId, Guid connectionId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetSupportReplyTemplateDraftQuery(tenantId, connectionId), cancellationToken));

    private static async Task<IResult> GetSupportFormalReplyDraftAsync(
        Guid tenantId, Guid connectionId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetSupportFormalReplyTemplateDraftQuery(tenantId, connectionId), cancellationToken));

    private static async Task<IResult> GetSupportSurveyDraftAsync(
        Guid tenantId, Guid connectionId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetSupportSurveyTemplateDraftQuery(tenantId, connectionId), cancellationToken));

    private static async Task<IResult> GetWholesaleInvitationNoticeDraftAsync(
        Guid tenantId, Guid connectionId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetWholesaleInvitationNoticeTemplateDraftQuery(tenantId, connectionId), cancellationToken));

    private static async Task<IResult> GetTaskNoticeDraftAsync(
        Guid tenantId, Guid connectionId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetTaskNoticeTemplateDraftQuery(tenantId, connectionId), cancellationToken));

    private static async Task<IResult> GetAppointmentReminderDraftAsync(
        Guid tenantId, Guid connectionId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetAppointmentReminderTemplateDraftQuery(tenantId, connectionId), cancellationToken));

    private static async Task<IResult> GetBillingDraftAsync(Guid tenantId, Guid connectionId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetBillingTemplatesDraftQuery(tenantId, connectionId), cancellationToken));

    // Sin cuerpo en la respuesta: el frontend quita la fila de su lista y no necesita nada más.
    private static async Task<IResult> DeleteAsync(Guid tenantId, Guid connectionId, Guid templateId, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(new DeleteTemplateCommand(tenantId, connectionId, templateId), cancellationToken);
        return Results.NoContent();
    }
}

/// <summary>Spec 2026-10-14 §4.7: el cuerpo de crear una plantilla libre. Todo opcional para que lo que falte salga 422 con su clave en
/// <c>errors</c>, no 400 del binder. Las variables se declaran en <c>body</c> como <c>{{nombre}}</c> y <c>examples</c> trae exactamente
/// esas claves. <c>headerImageFileId</c> (spec 2026-11-17) es la imagen fija del encabezado ya subida; excluye a <c>header</c>.</summary>
public sealed record CreateTemplateRequest(
    string? Name,
    string? Language,
    string? Category,
    string? Header,
    string? Body,
    string? Footer,
    List<TemplateButtonRequest>? Buttons,
    Dictionary<string, string?>? Examples,
    Guid? HeaderImageFileId = null);

/// <summary>Spec 2026-11-20: el archivo de la imagen nueva, ya subido por la sesión de la imagen. Nullable para que un cuerpo sin él salga
/// 422 con su clave en <c>errors</c>, no 400 del binder.</summary>
public sealed record ReplaceTemplateHeaderImageRequest(Guid? HeaderImageFileId);

/// <summary>El cuerpo editado de la plantilla de cotización. Son opcionales la propiedad <c>Body</c> y también el cuerpo HTTP entero: sin
/// ellos se crea el texto base (backend desplegado antes que el frontend).</summary>
public sealed record ProvisionQuotationTemplateRequest(string? Body);

/// <summary>Spec 2026-11-09 D3: el cuerpo editado de la plantilla de pedido, o nada para el texto base.</summary>
public sealed record ProvisionOrderTemplateRequest(string? Body);

/// <summary>Spec 2026-11-21: el cuerpo editado de la plantilla de factura, o nada para el texto base.</summary>
public sealed record ProvisionInvoiceTemplateRequest(string? Body);

/// <summary>Soporte fase 2: el cuerpo editado de la plantilla «Aviso de respuesta», o nada para el texto base.</summary>
public sealed record ProvisionSupportReplyTemplateRequest(string? Body);

/// <summary>Soporte fase 4b: el cuerpo editado de la plantilla «Respuesta formal», o nada para el texto base.</summary>
public sealed record ProvisionSupportFormalReplyTemplateRequest(string? Body);

/// <summary>Soporte fase 4b: el cuerpo editado de la plantilla de la encuesta, o nada para el texto base. Los botones no se editan.</summary>
public sealed record ProvisionSupportSurveyTemplateRequest(string? Body);

/// <summary>Mayorista: el cuerpo editado de la plantilla del aviso de invitación de distribuidor, o nada para el texto base.</summary>
public sealed record ProvisionWholesaleInvitationNoticeTemplateRequest(string? Body);

/// <summary>Support task notices: the edited body of the assignment notice template, or nothing for the base text.</summary>
public sealed record ProvisionTaskNoticeTemplateRequest(string? Body);

/// <summary>Agenda: el cuerpo editado de la plantilla del recordatorio de cita, o nada para el texto base.</summary>
public sealed record ProvisionAppointmentReminderTemplateRequest(string? Body);

/// <summary>Los cuerpos editados de las dos plantillas de cobro. Son opcionales cada propiedad y también el cuerpo HTTP entero: lo que falte
/// se crea con el texto base de su aviso (backend desplegado antes que el frontend).</summary>
public sealed record ProvisionBillingTemplatesRequest(string? ReminderBody, string? OverdueBody);

/// <summary><c>type</c> es <c>QuickReply</c>, <c>Url</c> o <c>PhoneNumber</c>, como <c>TemplateButtonDto</c>.</summary>
public sealed record TemplateButtonRequest(string? Type, string? Text, string? Url, string? PhoneNumber);

/// <summary>Spec 2026-11-17: la declaración de la imagen del encabezado. Todo opcional para que lo que falte salga 422 con su clave en
/// <c>errors</c>, no 400 del binder.</summary>
public sealed record CreateTemplateHeaderImageSessionRequest(string? FileName, string? MimeType, long SizeBytes);
