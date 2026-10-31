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

        // El cuerpo HTTP es opcional: sin él se crea el texto base de la definición (spec §4.5) y se adopta una externa compatible. Dejar
        // que se infiera requerido responde 500 cuando un frontend viejo no manda nada (precedente: el envío de cotizaciones).
        group.MapPost("/quotation", ProvisionQuotationAsync)
            .RequireAuthorization(MessagingPermissions.TemplateManage)
            .Accepts<ProvisionQuotationTemplateRequest>(isOptional: true, "application/json")
            .Produces<ManagedTemplateDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Cobros recurrentes §5: las dos plantillas de cobro, también sin cuerpo (su definición vive en el backend).
        group.MapPost("/billing", ProvisionBillingAsync)
            .RequireAuthorization(MessagingPermissions.TemplateManage)
            .Produces<IReadOnlyList<ManagedTemplateDto>>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
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
                    request.Examples),
                cancellationToken));

    // Location = la pantalla, como al crear una libre.
    private static async Task<IResult> ProvisionQuotationAsync(
        Guid tenantId, Guid connectionId, ProvisionQuotationTemplateRequest? request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Created(
            $"/api/v1/tenants/{tenantId}/messaging/connections/{connectionId}/template-management",
            await dispatcher.SendAsync(new ProvisionQuotationTemplateCommand(tenantId, connectionId, request?.Body), cancellationToken));

    // Location = la pantalla, como al provisionar la de cotización.
    private static async Task<IResult> ProvisionBillingAsync(Guid tenantId, Guid connectionId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Created(
            $"/api/v1/tenants/{tenantId}/messaging/connections/{connectionId}/template-management",
            await dispatcher.SendAsync(new ProvisionBillingTemplatesCommand(tenantId, connectionId), cancellationToken));

    // Sin cuerpo en la respuesta: el frontend quita la fila de su lista y no necesita nada más.
    private static async Task<IResult> DeleteAsync(Guid tenantId, Guid connectionId, Guid templateId, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(new DeleteTemplateCommand(tenantId, connectionId, templateId), cancellationToken);
        return Results.NoContent();
    }
}

/// <summary>Spec 2026-10-14 §4.7: el cuerpo de crear una plantilla libre. Todo opcional para que lo que falte salga 422 con su clave en
/// <c>errors</c>, no 400 del binder. Las variables se declaran en <c>body</c> como <c>{{nombre}}</c> y <c>examples</c> trae exactamente
/// esas claves.</summary>
public sealed record CreateTemplateRequest(
    string? Name,
    string? Language,
    string? Category,
    string? Header,
    string? Body,
    string? Footer,
    List<TemplateButtonRequest>? Buttons,
    Dictionary<string, string?>? Examples);

/// <summary>El cuerpo editado de la plantilla de cotización. Son opcionales la propiedad <c>Body</c> y también el cuerpo HTTP entero: sin
/// ellos se crea el texto base (backend desplegado antes que el frontend).</summary>
public sealed record ProvisionQuotationTemplateRequest(string? Body);

/// <summary><c>type</c> es <c>QuickReply</c>, <c>Url</c> o <c>PhoneNumber</c>, como <c>TemplateButtonDto</c>.</summary>
public sealed record TemplateButtonRequest(string? Type, string? Text, string? Url, string? PhoneNumber);
