using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Hr.Application;

namespace Modules.Hr.Api;

public static class HrEndpoints
{
    public static IEndpointRouteBuilder MapHrEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Tenant en la ruta, como loans y billing: la política frena a quien no tiene el permiso y el handler
        // (HrAuthorization) a quien lo tiene en otro tenant. El resto de las rutas llega con las Tareas 7 a 10.
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/hr")
            .WithTags("Hr");

        group.MapGet("/salary-rules", GetSalaryRulesAsync)
            .RequireAuthorization(HrPermissions.ContractRead)
            .Produces<SalaryRulesDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        MapSettings(group);
        MapFiles(group);

        return endpoints;
    }

    // Configuración de la organización: leer es `hr.employee.read`; toda escritura es `hr.settings.manage` (y el handler exige
    // también el `read`, D-1). El `PUT` de cada una exige `If-Match` con su propia versión.
    private static void MapSettings(RouteGroupBuilder group)
    {
        group.MapGet("/settings", GetSettingsAsync)
            .RequireAuthorization(HrPermissions.EmployeeRead)
            .Produces<HrSettingsDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/sites", CreateSiteAsync)
            .RequireAuthorization(HrPermissions.SettingsManage)
            .Accepts<SiteRequest>("application/json")
            .Produces<SiteDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/sites/{id:guid}", UpdateSiteAsync)
            .RequireAuthorization(HrPermissions.SettingsManage)
            .Accepts<SiteRequest>("application/json")
            .Produces<SiteDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapPost("/workplaces", CreateWorkplaceAsync)
            .RequireAuthorization(HrPermissions.SettingsManage)
            .Accepts<WorkplaceRequest>("application/json")
            .Produces<WorkplaceDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/workplaces/{id:guid}", UpdateWorkplaceAsync)
            .RequireAuthorization(HrPermissions.SettingsManage)
            .Accepts<WorkplaceRequest>("application/json")
            .Produces<WorkplaceDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapPost("/areas", (Guid tenantId, NamedSettingRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                CreateNamedAsync(tenantId, NamedSettingKind.Area, "areas", request, dispatcher, cancellationToken))
            .RequireAuthorization(HrPermissions.SettingsManage)
            .Accepts<NamedSettingRequest>("application/json")
            .Produces<AreaDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/areas/{id:guid}", (Guid tenantId, Guid id, NamedSettingRequest request, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                UpdateNamedAsync(tenantId, NamedSettingKind.Area, id, request, httpContext, dispatcher, cancellationToken))
            .RequireAuthorization(HrPermissions.SettingsManage)
            .Accepts<NamedSettingRequest>("application/json")
            .Produces<AreaDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapPost("/positions", (Guid tenantId, NamedSettingRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                CreateNamedAsync(tenantId, NamedSettingKind.Position, "positions", request, dispatcher, cancellationToken))
            .RequireAuthorization(HrPermissions.SettingsManage)
            .Accepts<NamedSettingRequest>("application/json")
            .Produces<PositionDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/positions/{id:guid}", (Guid tenantId, Guid id, NamedSettingRequest request, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                UpdateNamedAsync(tenantId, NamedSettingKind.Position, id, request, httpContext, dispatcher, cancellationToken))
            .RequireAuthorization(HrPermissions.SettingsManage)
            .Accepts<NamedSettingRequest>("application/json")
            .Produces<PositionDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        // Archivar, desarchivar y eliminar: las mismas tres rutas para los cuatro tipos.
        foreach (var (segment, kind) in new[]
                 {
                     ("sites", SettingKind.Site),
                     ("workplaces", SettingKind.Workplace),
                     ("areas", SettingKind.Area),
                     ("positions", SettingKind.Position),
                 })
        {
            var settingKind = kind;
            group.MapPost($"/{segment}/{{id:guid}}/archive", (Guid tenantId, Guid id, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                    ArchiveAsync(tenantId, settingKind, id, archive: true, dispatcher, cancellationToken))
                .RequireAuthorization(HrPermissions.SettingsManage)
                .Produces(StatusCodes.Status200OK)
                .ProducesProblem(StatusCodes.Status403Forbidden)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict)
                .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

            group.MapPost($"/{segment}/{{id:guid}}/unarchive", (Guid tenantId, Guid id, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                    ArchiveAsync(tenantId, settingKind, id, archive: false, dispatcher, cancellationToken))
                .RequireAuthorization(HrPermissions.SettingsManage)
                .Produces(StatusCodes.Status200OK)
                .ProducesProblem(StatusCodes.Status403Forbidden)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict)
                .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

            group.MapDelete($"/{segment}/{{id:guid}}", (Guid tenantId, Guid id, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                    DeleteAsync(tenantId, settingKind, id, dispatcher, cancellationToken))
                .RequireAuthorization(HrPermissions.SettingsManage)
                .Produces(StatusCodes.Status204NoContent)
                .ProducesProblem(StatusCodes.Status403Forbidden)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict);
        }
    }

    // Los PDF: abrir la sesión es `hr.contract.manage` (y el handler exige también el `read`); bajar es `hr.contract.read`.
    private static void MapFiles(RouteGroupBuilder group)
    {
        group.MapPost("/files/upload-session", OpenUploadSessionAsync)
            .RequireAuthorization(HrPermissions.ContractManage)
            .Accepts<HrUploadSessionRequest>("application/json")
            .Produces<HrUploadSessionDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/files/{fileId:guid}", GetFileAsync)
            .RequireAuthorization(HrPermissions.ContractRead)
            .Produces<HrDownloadUrlDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> OpenUploadSessionAsync(
        Guid tenantId, HrUploadSessionRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        // Un campo ausente es un archivo inválido: el handler lo rechaza con hr.file.invalid.
        var session = await dispatcher.SendAsync(
            new OpenHrUploadSessionCommand(tenantId, request.FileName ?? string.Empty, request.MimeType ?? string.Empty, request.SizeBytes ?? 0),
            cancellationToken);

        return Results.Ok(session);
    }

    private static async Task<IResult> GetFileAsync(
        Guid tenantId, Guid fileId, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var url = await dispatcher.QueryAsync(new GetHrFileDownloadUrlQuery(tenantId, fileId), cancellationToken);

        return Results.Ok(url);
    }

    // La fecha llega como texto y el handler la lee, ya autorizado: un valor mal formado que dejara al binding automático
    // lanzaría una BadHttpRequestException, y ApiExceptionHandler la reduce a 500.
    private static async Task<IResult> GetSalaryRulesAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        string? date = null)
    {
        var rules = await dispatcher.QueryAsync(new GetSalaryRulesQuery(tenantId, date), cancellationToken);

        return Results.Ok(rules);
    }

    private static async Task<IResult> GetSettingsAsync(
        Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var settings = await dispatcher.QueryAsync(new GetHrSettingsQuery(tenantId), cancellationToken);

        return Results.Ok(settings);
    }

    private static async Task<IResult> CreateSiteAsync(
        Guid tenantId, SiteRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var site = await dispatcher.SendAsync(new CreateSiteCommand(tenantId, request), cancellationToken);

        return Results.Created($"/api/v1/tenants/{tenantId}/hr/sites/{site.Id}", site);
    }

    private static async Task<IResult> UpdateSiteAsync(
        Guid tenantId,
        Guid id,
        SiteRequest request,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var site = await dispatcher.SendAsync(
            new UpdateSiteCommand(tenantId, id, request, IfMatchOf(httpContext)), cancellationToken);

        return Results.Ok(site);
    }

    private static async Task<IResult> CreateWorkplaceAsync(
        Guid tenantId, WorkplaceRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var workplace = await dispatcher.SendAsync(new CreateWorkplaceCommand(tenantId, request), cancellationToken);

        return Results.Created($"/api/v1/tenants/{tenantId}/hr/workplaces/{workplace.Id}", workplace);
    }

    private static async Task<IResult> UpdateWorkplaceAsync(
        Guid tenantId,
        Guid id,
        WorkplaceRequest request,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var workplace = await dispatcher.SendAsync(
            new UpdateWorkplaceCommand(tenantId, id, request, IfMatchOf(httpContext)), cancellationToken);

        return Results.Ok(workplace);
    }

    private static async Task<IResult> CreateNamedAsync(
        Guid tenantId,
        NamedSettingKind kind,
        string segment,
        NamedSettingRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var setting = await dispatcher.SendAsync(new CreateNamedSettingCommand(tenantId, kind, request), cancellationToken);
        var id = setting switch
        {
            AreaDto area => area.Id,
            PositionDto position => position.Id,
            _ => throw new InvalidOperationException($"Unexpected setting response '{setting.GetType().Name}'."),
        };

        return Results.Created($"/api/v1/tenants/{tenantId}/hr/{segment}/{id}", setting);
    }

    private static async Task<IResult> UpdateNamedAsync(
        Guid tenantId,
        NamedSettingKind kind,
        Guid id,
        NamedSettingRequest request,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var setting = await dispatcher.SendAsync(
            new UpdateNamedSettingCommand(tenantId, kind, id, request, IfMatchOf(httpContext)), cancellationToken);

        return Results.Ok(setting);
    }

    private static async Task<IResult> ArchiveAsync(
        Guid tenantId,
        SettingKind kind,
        Guid id,
        bool archive,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var setting = await dispatcher.SendAsync(new ArchiveSettingCommand(tenantId, kind, id, archive), cancellationToken);

        return Results.Ok(setting);
    }

    private static async Task<IResult> DeleteAsync(
        Guid tenantId, SettingKind kind, Guid id, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(new DeleteSettingCommand(tenantId, kind, id), cancellationToken);

        return Results.NoContent();
    }

    // El If-Match llega crudo al caso de uso, que lo exige ya autorizado: parsearlo acá daría 428 a quien opera en otro tenant.
    private static string? IfMatchOf(HttpContext httpContext) => httpContext.Request.Headers.IfMatch.ToString();
}
