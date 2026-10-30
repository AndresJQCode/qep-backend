using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Billing.Application;

namespace Modules.Billing.Api;

public static class BillingEndpoints
{
    public static IEndpointRouteBuilder MapBillingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Tenant en la ruta, como suppliers y customers: la política frena a quien no tiene el
        // permiso y el handler (BillingAuthorization) a quien lo tiene en otro tenant. Las demás
        // rutas del módulo se agregan en las tareas siguientes, sobre este mismo grupo.
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/billing")
            .WithTags("Billing");

        group.MapGet("/customers/{customerId:guid}/profile", GetCustomerProfileAsync)
            .RequireAuthorization(BillingPermissions.Read)
            .Produces<ProfileDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Upsert sin If-Match (spec §4): la última escritura gana, como los ajustes de cotización.
        group.MapPut("/customers/{customerId:guid}/profile", UpsertCustomerProfileAsync)
            .RequireAuthorization(BillingPermissions.Manage)
            .Accepts<ProfileInput>("application/json")
            .Produces<ProfileDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/assignees", ListAssigneesAsync)
            .RequireAuthorization(BillingPermissions.Read)
            .Produces<AssigneesDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        // Servicios recurrentes (spec §4). preview-charges va antes de /{serviceId:guid}: son rutas literales distintas, pero
        // así el orden de lectura es el del contrato.
        group.MapGet("/services", ListServicesAsync)
            .RequireAuthorization(BillingPermissions.Read)
            .Produces<ServicePageDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/services/preview-charges", PreviewServiceChargesAsync)
            .RequireAuthorization(BillingPermissions.Read)
            .Produces<ChargesPreviewDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/services/{serviceId:guid}", GetServiceAsync)
            .RequireAuthorization(BillingPermissions.Read)
            .Produces<ServiceDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/services", CreateServiceAsync)
            .RequireAuthorization(BillingPermissions.Manage)
            .Accepts<CreateServiceRequest>("application/json")
            .Produces<ServiceDetailDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/services/{serviceId:guid}", UpdateServiceAsync)
            .RequireAuthorization(BillingPermissions.Manage)
            .Accepts<UpdateServiceRequest>("application/json")
            .Produces<ServiceDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        MapTransition(group, "pause", BillingServiceTransition.Pause);
        MapTransition(group, "resume", BillingServiceTransition.Resume);
        MapTransition(group, "cancel", BillingServiceTransition.Cancel);

        // Cobros (spec §4). La lista y el detalle leen; marcar pagado y corregir el pago gestionan. Sin If-Match: pagar dos
        // veces es 422 not_pending, no 412.
        group.MapGet("/charges", ListChargesAsync)
            .RequireAuthorization(BillingPermissions.Read)
            .Produces<ChargePageDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/charges/{chargeId:guid}", GetChargeAsync)
            .RequireAuthorization(BillingPermissions.Read)
            .Produces<ChargeDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/charges/{chargeId:guid}/pay", PayChargeAsync)
            .RequireAuthorization(BillingPermissions.Manage)
            .Accepts<PayChargeRequest>("application/json")
            .Produces<ChargeDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/charges/{chargeId:guid}/payment", UpdateChargePaymentAsync)
            .RequireAuthorization(BillingPermissions.Manage)
            .Accepts<PayChargeRequest>("application/json")
            .Produces<ChargeDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Ajustes (spec §4): sólo manage, también para leer. Sin If-Match, la última escritura gana.
        group.MapGet("/settings", GetSettingsAsync)
            .RequireAuthorization(BillingPermissions.Manage)
            .Produces<BillingSettingsDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPut("/settings", UpdateSettingsAsync)
            .RequireAuthorization(BillingPermissions.Manage)
            .Accepts<UpdateBillingSettingsRequest>("application/json")
            .Produces<BillingSettingsDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Importación desde Excel (§3.2b, §4). Todo con manage, también la plantilla, como /customers/import. Antiforgery
        // apagado como CustomerEndpoints: el CSRF lo frena X-Qep-Client, no el token de formulario.
        group.MapGet("/import/template", GetImportTemplateAsync)
            .RequireAuthorization(BillingPermissions.Manage)
            .Produces(StatusCodes.Status200OK, contentType: ExcelContentType)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/import/preview", PreviewImportAsync)
            .RequireAuthorization(BillingPermissions.Manage)
            .Produces<ImportPreviewDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .DisableAntiforgery();

        group.MapPost("/import", ImportAsync)
            .RequireAuthorization(BillingPermissions.Manage)
            .Produces<ImportResultDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .DisableAntiforgery();

        return endpoints;
    }

    private const string ExcelContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static async Task<IResult> GetImportTemplateAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var template = await dispatcher.QueryAsync(new GetBillingImportTemplateQuery(tenantId), cancellationToken);

        return Results.File(template.Content, ExcelContentType, template.FileName);
    }

    // IFormFile anulable: sin archivo el handler responde 422 billing.import.file_required y no un 400 del binding.
    private static async Task<IResult> PreviewImportAsync(
        Guid tenantId,
        IFormFile? file,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        await using var content = file?.OpenReadStream() ?? Stream.Null;
        var preview = await dispatcher.SendAsync(
            new PreviewBillingImportCommand(tenantId, file?.FileName ?? string.Empty, file?.Length ?? 0, content),
            cancellationToken);

        return Results.Ok(preview);
    }

    private static async Task<IResult> ImportAsync(
        Guid tenantId,
        IFormFile? file,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        await using var content = file?.OpenReadStream() ?? Stream.Null;
        var result = await dispatcher.SendAsync(
            new ImportBillingServicesCommand(tenantId, file?.FileName ?? string.Empty, file?.Length ?? 0, content),
            cancellationToken);

        return Results.Ok(result);
    }

    private static async Task<IResult> GetSettingsAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetBillingSettingsQuery(tenantId), cancellationToken));

    private static async Task<IResult> UpdateSettingsAsync(
        Guid tenantId,
        UpdateBillingSettingsRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(UpdateBillingSettingsCommand.From(tenantId, request), cancellationToken));

    private static async Task<IResult> GetCustomerProfileAsync(
        Guid tenantId,
        Guid customerId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var profile = await dispatcher.QueryAsync(
            new GetCustomerProfileQuery(tenantId, customerId), cancellationToken);

        return Results.Ok(profile);
    }

    private static async Task<IResult> UpsertCustomerProfileAsync(
        Guid tenantId,
        Guid customerId,
        ProfileInput request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var profile = await dispatcher.SendAsync(
            UpsertCustomerProfileCommand.From(tenantId, customerId, request), cancellationToken);

        return Results.Ok(profile);
    }

    private static async Task<IResult> ListAssigneesAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var assignees = await dispatcher.QueryAsync(new ListAssigneesQuery(tenantId), cancellationToken);

        return Results.Ok(assignees);
    }

    private static void MapTransition(RouteGroupBuilder group, string verb, BillingServiceTransition transition) =>
        group.MapPost(
                $"/services/{{serviceId:guid}}/{verb}",
                async (Guid tenantId, Guid serviceId, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                    Results.Ok(await dispatcher.SendAsync(
                        new ChangeServiceStatusCommand(tenantId, serviceId, transition, RequireVersion(httpContext)), cancellationToken)))
            .RequireAuthorization(BillingPermissions.Manage)
            .Produces<ServiceDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

    private static async Task<IResult> ListServicesAsync(
        Guid tenantId,
        Guid? customerId,
        string? status,
        string? search,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        int page = 1,
        int pageSize = ListServicesQuery.DefaultPageSize)
    {
        var services = await dispatcher.QueryAsync(
            new ListServicesQuery(
                tenantId,
                customerId,
                string.IsNullOrWhiteSpace(status) ? ListServicesQuery.DefaultStatus : status,
                search,
                page,
                pageSize),
            cancellationToken);

        return Results.Ok(services);
    }

    private static async Task<IResult> PreviewServiceChargesAsync(
        Guid tenantId,
        string? cycle,
        string? firstDueDate,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var preview = await dispatcher.QueryAsync(
            PreviewServiceChargesQuery.From(tenantId, cycle, firstDueDate), cancellationToken);

        return Results.Ok(preview);
    }

    private static async Task<IResult> GetServiceAsync(
        Guid tenantId,
        Guid serviceId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var service = await dispatcher.QueryAsync(new GetServiceQuery(tenantId, serviceId), cancellationToken);

        return Results.Ok(service);
    }

    private static async Task<IResult> CreateServiceAsync(
        Guid tenantId,
        CreateServiceRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var service = await dispatcher.SendAsync(CreateServiceCommand.From(tenantId, request), cancellationToken);

        return Results.Created($"/api/v1/tenants/{tenantId}/billing/services/{service.Service.Id}", service);
    }

    private static async Task<IResult> UpdateServiceAsync(
        Guid tenantId,
        Guid serviceId,
        UpdateServiceRequest request,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var service = await dispatcher.SendAsync(
            UpdateServiceCommand.From(tenantId, serviceId, request, RequireVersion(httpContext)), cancellationToken);

        return Results.Ok(service);
    }

    private static async Task<IResult> ListChargesAsync(
        Guid tenantId,
        Guid? customerId,
        string? status,
        string? assignee,
        string? search,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        int page = 1,
        int pageSize = ListChargesQuery.DefaultPageSize)
    {
        var charges = await dispatcher.QueryAsync(
            new ListChargesQuery(
                tenantId,
                string.IsNullOrWhiteSpace(status) ? ListChargesQuery.DefaultStatus : status,
                string.Equals(assignee?.Trim(), "me", StringComparison.OrdinalIgnoreCase),
                customerId,
                search,
                page,
                pageSize),
            cancellationToken);

        return Results.Ok(charges);
    }

    private static async Task<IResult> GetChargeAsync(
        Guid tenantId,
        Guid chargeId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var charge = await dispatcher.QueryAsync(new GetChargeQuery(tenantId, chargeId), cancellationToken);

        return Results.Ok(charge);
    }

    private static async Task<IResult> PayChargeAsync(
        Guid tenantId,
        Guid chargeId,
        PayChargeRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var charge = await dispatcher.SendAsync(PayChargeCommand.From(tenantId, chargeId, request), cancellationToken);

        return Results.Ok(charge);
    }

    private static async Task<IResult> UpdateChargePaymentAsync(
        Guid tenantId,
        Guid chargeId,
        PayChargeRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var charge = await dispatcher.SendAsync(UpdateChargePaymentCommand.From(tenantId, chargeId, request), cancellationToken);

        return Results.Ok(charge);
    }

    // Copia de MessagingEndpoints.RequireVersion (Billing no referencia Messaging): sin If-Match 428; la versión vieja es 412 en el handler.
    private static long RequireVersion(HttpContext httpContext) =>
        TryParseVersion(httpContext.Request.Headers.IfMatch, out var version)
            ? version
            : throw new PreconditionRequiredException(
                "precondition.if_match_required",
                "A valid If-Match header containing the loaded service version is required.");

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
