using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modules.Payroll.Application;

namespace Modules.Payroll.Api;

public static class PayrollEndpoints
{
    // The analyzer rejects a file over 5 MB only after the form was buffered; the body limit is the file limit plus room for the multipart
    // envelope, and it stops a hostile upload before it is read.
    private const long ImportBodyMaxBytes = InputsImportColumns.MaxSizeInBytes + (1024 * 1024);

    public static IEndpointRouteBuilder MapPayrollEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Tenant en la ruta, como hr y loans: la política frena a quien no tiene el permiso y el handler (PayrollAuthorization) a
        // quien lo tiene en otro tenant. La política de cada ruta es el permiso en negrita de la tabla de rutas del contrato.
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/payroll")
            .WithTags("Payroll");

        MapSettings(group);
        MapPeriods(group);
        MapRecurring(group);
        MapVacations(group);

        return endpoints;
    }

    // Vacaciones disfrutadas de un empleado. Leer es `period.read`; previsualizar y escribir, `period.read` + `period.manage`
    // (una novedad de nómina, no la ficha del empleado). El `PUT` y el `DELETE` exigen `If-Match`; crear y previsualizar, no.
    private static void MapVacations(RouteGroupBuilder group)
    {
        const string route = "/companies/{companyId:guid}/employees/{employeeId:guid}/vacations";

        group.MapGet(route, ListVacationsAsync)
            .RequireAuthorization(PayrollPermissions.PeriodRead)
            .Produces<IReadOnlyList<VacationDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost(route + "/preview", PreviewVacationAsync)
            .RequireAuthorization(PayrollPermissions.PeriodManage)
            .Accepts<VacationPreviewRequest>("application/json")
            .Produces<VacationPreviewDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost(route, CreateVacationAsync)
            .RequireAuthorization(PayrollPermissions.PeriodManage)
            .Accepts<VacationRequest>("application/json")
            .Produces<IReadOnlyList<VacationDto>>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPut(route + "/{vacationId:guid}", UpdateVacationAsync)
            .RequireAuthorization(PayrollPermissions.PeriodManage)
            .Accepts<VacationRequest>("application/json")
            .Produces<IReadOnlyList<VacationDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapDelete(route + "/{vacationId:guid}", DeleteVacationAsync)
            .RequireAuthorization(PayrollPermissions.PeriodManage)
            .Produces<IReadOnlyList<VacationDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);
    }

    private static async Task<IResult> CreateVacationAsync(
        Guid tenantId,
        Guid companyId,
        Guid employeeId,
        VacationRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var list = await dispatcher.SendAsync(new CreateVacationCommand(tenantId, companyId, employeeId, request), cancellationToken);

        return Results.Created($"/api/v1/tenants/{tenantId}/payroll/companies/{companyId}/employees/{employeeId}/vacations", list);
    }

    private static async Task<IResult> UpdateVacationAsync(
        Guid tenantId,
        Guid companyId,
        Guid employeeId,
        Guid vacationId,
        VacationRequest request,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new UpdateVacationCommand(tenantId, companyId, employeeId, vacationId, request, IfMatchOf(httpContext)), cancellationToken));

    private static async Task<IResult> DeleteVacationAsync(
        Guid tenantId,
        Guid companyId,
        Guid employeeId,
        Guid vacationId,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new DeleteVacationCommand(tenantId, companyId, employeeId, vacationId, IfMatchOf(httpContext)), cancellationToken));

    private static async Task<IResult> ListVacationsAsync(
        Guid tenantId, Guid companyId, Guid employeeId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListVacationsQuery(tenantId, companyId, employeeId), cancellationToken));

    private static async Task<IResult> PreviewVacationAsync(
        Guid tenantId,
        Guid companyId,
        Guid employeeId,
        VacationPreviewRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new PreviewVacationQuery(tenantId, companyId, employeeId, request), cancellationToken));

    // Períodos. `ensure-current` abre las quincenas que faltan: es de quien administra (I-6) y el handler exige también `read`;
    // quien sólo lee usa `GET periods`, que no crea nada.
    private static void MapPeriods(RouteGroupBuilder group)
    {
        group.MapPost("/companies/{companyId:guid}/periods/ensure-current", EnsureCurrentAsync)
            .RequireAuthorization(PayrollPermissions.PeriodManage)
            .Produces<PayrollPeriodListDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/companies/{companyId:guid}/periods/open-next", OpenNextAsync)
            .RequireAuthorization(PayrollPermissions.PeriodManage)
            .Produces<PayrollPeriodListDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/companies/{companyId:guid}/periods", ListPeriodsAsync)
            .RequireAuthorization(PayrollPermissions.PeriodRead)
            .Produces<PayrollPeriodListDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/periods/{periodId:guid}", GetPeriodAsync)
            .RequireAuthorization(PayrollPermissions.PeriodRead)
            .Produces<PayrollPeriodDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/periods/{periodId:guid}/export", ExportPeriodAsync)
            .RequireAuthorization(PayrollPermissions.PeriodRead)
            .Produces(StatusCodes.Status200OK, contentType: ExcelContentType)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Importación de novedades: las tres rutas van antes de `{p}/employees/{employeeId:guid}`. Antiforgery apagado como en Hr: el
        // CSRF lo frena X-Qep-Client, no el token de formulario.
        group.MapGet("/periods/{periodId:guid}/inputs/template", GetInputsTemplateAsync)
            .RequireAuthorization(PayrollPermissions.PeriodManage)
            .Produces(StatusCodes.Status200OK, contentType: ExcelContentType)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/periods/{periodId:guid}/inputs/import/preview", PreviewInputsImportAsync)
            .RequireAuthorization(PayrollPermissions.PeriodManage)
            .Produces<InputsImportPreviewDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithMetadata(new RequestSizeLimitAttribute(ImportBodyMaxBytes))
            .DisableAntiforgery();

        group.MapPost("/periods/{periodId:guid}/inputs/import", ImportInputsAsync)
            .RequireAuthorization(PayrollPermissions.PeriodManage)
            .Produces<PayrollPeriodDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .WithMetadata(new RequestSizeLimitAttribute(ImportBodyMaxBytes))
            .DisableAntiforgery();

        group.MapGet("/periods/{periodId:guid}/employees/{employeeId:guid}", GetPeriodEmployeeAsync)
            .RequireAuthorization(PayrollPermissions.PeriodRead)
            .Produces<PayrollEmployeeDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/periods/{periodId:guid}/employees/{employeeId:guid}/inputs", UpdateInputsAsync)
            .RequireAuthorization(PayrollPermissions.PeriodManage)
            .Accepts<EmployeeInputsRequest>("application/json")
            .Produces<PayrollEmployeeDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapPost("/periods/{periodId:guid}/liquidate", LiquidateAsync)
            .RequireAuthorization(PayrollPermissions.PeriodManage)
            .Accepts<LiquidateRequest>("application/json")
            .Produces<PayrollPeriodDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapPost("/periods/{periodId:guid}/approve", ApproveAsync)
            .RequireAuthorization(PayrollPermissions.PeriodApprove)
            .Produces<PayrollPeriodDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapPost("/periods/{periodId:guid}/close", CloseAsync)
            .RequireAuthorization(PayrollPermissions.PeriodManage)
            .Produces<PayrollPeriodDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapPost("/periods/{periodId:guid}/reopen", ReopenAsync)
            .RequireAuthorization(PayrollPermissions.PeriodManage)
            .Accepts<ReopenRequest>("application/json")
            .Produces<PayrollPeriodDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);
    }

    private static async Task<IResult> LiquidateAsync(
        Guid tenantId, Guid periodId, LiquidateRequest? request, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new LiquidatePeriodCommand(tenantId, periodId, request, IfMatchOf(httpContext)), cancellationToken));

    private static async Task<IResult> ApproveAsync(
        Guid tenantId, Guid periodId, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new ApprovePeriodCommand(tenantId, periodId, IfMatchOf(httpContext)), cancellationToken));

    private static async Task<IResult> CloseAsync(
        Guid tenantId, Guid periodId, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new ClosePeriodCommand(tenantId, periodId, IfMatchOf(httpContext)), cancellationToken));

    private static async Task<IResult> ReopenAsync(
        Guid tenantId, Guid periodId, ReopenRequest? request, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new ReopenPeriodCommand(tenantId, periodId, request ?? new ReopenRequest(null), IfMatchOf(httpContext)), cancellationToken));

    private const string ExcelContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static async Task<IResult> ExportPeriodAsync(
        Guid tenantId, Guid periodId, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var file = await dispatcher.QueryAsync(new ExportPeriodQuery(tenantId, periodId), cancellationToken);

        return Results.File(file.Content, ExcelContentType, file.FileName);
    }

    private static async Task<IResult> GetInputsTemplateAsync(
        Guid tenantId, Guid periodId, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var file = await dispatcher.QueryAsync(new GetInputsImportTemplateQuery(tenantId, periodId), cancellationToken);

        return Results.File(file.Content, ExcelContentType, file.FileName);
    }

    private static async Task<IResult> PreviewInputsImportAsync(
        Guid tenantId, Guid periodId, IFormFile? file, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        await using var content = file?.OpenReadStream() ?? Stream.Null;
        var preview = await dispatcher.SendAsync(
            new PreviewInputsImportCommand(tenantId, periodId, file?.FileName ?? string.Empty, file?.Length ?? 0, content), cancellationToken);

        return Results.Ok(preview);
    }

    private static async Task<IResult> ImportInputsAsync(
        Guid tenantId,
        Guid periodId,
        IFormFile? file,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        await using var content = file?.OpenReadStream() ?? Stream.Null;
        var detail = await dispatcher.SendAsync(
            new ImportInputsCommand(
                tenantId, periodId, file?.FileName ?? string.Empty, file?.Length ?? 0, content, IfMatchOf(httpContext)),
            cancellationToken);

        return Results.Ok(detail);
    }

    private static async Task<IResult> GetPeriodEmployeeAsync(
        Guid tenantId, Guid periodId, Guid employeeId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetPeriodEmployeeQuery(tenantId, periodId, employeeId), cancellationToken));

    private static async Task<IResult> UpdateInputsAsync(
        Guid tenantId,
        Guid periodId,
        Guid employeeId,
        EmployeeInputsRequest request,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new UpdateEmployeeInputsCommand(tenantId, periodId, employeeId, request, IfMatchOf(httpContext)), cancellationToken));

    // Recurrentes de la ficha de un empleado. Leer acepta `period.read` o `settings.manage` (SettingsView); toda escritura es sólo
    // `settings.manage` (decisión del owner). El `PUT` exige `If-Match`; activar y desactivar, no (I-8).
    private static void MapRecurring(RouteGroupBuilder group)
    {
        const string route = "/companies/{companyId:guid}/employees/{employeeId:guid}/recurring-concepts";

        group.MapGet(route, ListRecurringAsync)
            .RequireAuthorization(PayrollPolicies.SettingsView)
            .Produces<IReadOnlyList<RecurringConceptDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost(route, CreateRecurringAsync)
            .RequireAuthorization(PayrollPermissions.SettingsManage)
            .Accepts<RecurringConceptRequest>("application/json")
            .Produces<IReadOnlyList<RecurringConceptDto>>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPut(route + "/{recurringId:guid}", UpdateRecurringAsync)
            .RequireAuthorization(PayrollPermissions.SettingsManage)
            .Accepts<RecurringConceptRequest>("application/json")
            .Produces<IReadOnlyList<RecurringConceptDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapPost(route + "/{recurringId:guid}/activate", (Guid tenantId, Guid companyId, Guid employeeId, Guid recurringId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                ToggleRecurringAsync(tenantId, companyId, employeeId, recurringId, activate: true, dispatcher, cancellationToken))
            .RequireAuthorization(PayrollPermissions.SettingsManage)
            .Produces<IReadOnlyList<RecurringConceptDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost(route + "/{recurringId:guid}/deactivate", (Guid tenantId, Guid companyId, Guid employeeId, Guid recurringId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                ToggleRecurringAsync(tenantId, companyId, employeeId, recurringId, activate: false, dispatcher, cancellationToken))
            .RequireAuthorization(PayrollPermissions.SettingsManage)
            .Produces<IReadOnlyList<RecurringConceptDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> ListRecurringAsync(
        Guid tenantId, Guid companyId, Guid employeeId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListRecurringConceptsQuery(tenantId, companyId, employeeId), cancellationToken));

    private static async Task<IResult> CreateRecurringAsync(
        Guid tenantId,
        Guid companyId,
        Guid employeeId,
        RecurringConceptRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var list = await dispatcher.SendAsync(
            new CreateRecurringConceptCommand(tenantId, companyId, employeeId, request), cancellationToken);

        return Results.Created($"/api/v1/tenants/{tenantId}/payroll/companies/{companyId}/employees/{employeeId}/recurring-concepts", list);
    }

    private static async Task<IResult> UpdateRecurringAsync(
        Guid tenantId,
        Guid companyId,
        Guid employeeId,
        Guid recurringId,
        RecurringConceptRequest request,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new UpdateRecurringConceptCommand(tenantId, companyId, employeeId, recurringId, request, IfMatchOf(httpContext)),
            cancellationToken));

    private static async Task<IResult> ToggleRecurringAsync(
        Guid tenantId,
        Guid companyId,
        Guid employeeId,
        Guid recurringId,
        bool activate,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new ToggleRecurringConceptCommand(tenantId, companyId, employeeId, recurringId, activate), cancellationToken));

    private static async Task<IResult> EnsureCurrentAsync(
        Guid tenantId, Guid companyId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new EnsureCurrentPeriodsCommand(tenantId, companyId), cancellationToken));

    private static async Task<IResult> OpenNextAsync(
        Guid tenantId, Guid companyId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new OpenNextPeriodCommand(tenantId, companyId), cancellationToken));

    private static async Task<IResult> ListPeriodsAsync(
        Guid tenantId, Guid companyId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListPeriodsQuery(tenantId, companyId), cancellationToken));

    private static async Task<IResult> GetPeriodAsync(
        Guid tenantId, Guid periodId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetPeriodQuery(tenantId, periodId), cancellationToken));

    // Empresas, configuración y conceptos. Leer acepta cualquiera de `payroll.period.read` o `payroll.settings.manage`
    // (PayrollPolicies.SettingsView); toda escritura es sólo `payroll.settings.manage`. El `PUT` exige `If-Match`; archivar y
    // desarchivar, no.
    private static void MapSettings(RouteGroupBuilder group)
    {
        group.MapGet("/companies", ListCompaniesAsync)
            .RequireAuthorization(PayrollPolicies.SettingsView)
            .Produces<IReadOnlyList<PayrollCompanyDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/companies/{companyId:guid}/settings", GetSettingsAsync)
            .RequireAuthorization(PayrollPolicies.SettingsView)
            .Produces<PayrollSettingsDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/companies/{companyId:guid}/settings", UpdateSettingsAsync)
            .RequireAuthorization(PayrollPermissions.SettingsManage)
            .Accepts<PayrollSettingsRequest>("application/json")
            .Produces<PayrollSettingsDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapPost("/companies/{companyId:guid}/concepts", CreateConceptAsync)
            .RequireAuthorization(PayrollPermissions.SettingsManage)
            .Accepts<ConceptRequest>("application/json")
            .Produces<PayrollConceptDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/companies/{companyId:guid}/concepts/{conceptId:guid}", UpdateConceptAsync)
            .RequireAuthorization(PayrollPermissions.SettingsManage)
            .Accepts<ConceptRequest>("application/json")
            .Produces<PayrollConceptDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapPost("/companies/{companyId:guid}/concepts/{conceptId:guid}/archive", (Guid tenantId, Guid companyId, Guid conceptId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                ArchiveConceptAsync(tenantId, companyId, conceptId, archive: true, dispatcher, cancellationToken))
            .RequireAuthorization(PayrollPermissions.SettingsManage)
            .Produces<PayrollConceptDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/companies/{companyId:guid}/concepts/{conceptId:guid}/unarchive", (Guid tenantId, Guid companyId, Guid conceptId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                ArchiveConceptAsync(tenantId, companyId, conceptId, archive: false, dispatcher, cancellationToken))
            .RequireAuthorization(PayrollPermissions.SettingsManage)
            .Produces<PayrollConceptDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    private static async Task<IResult> ListCompaniesAsync(
        Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListPayrollCompaniesQuery(tenantId), cancellationToken));

    private static async Task<IResult> GetSettingsAsync(
        Guid tenantId, Guid companyId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetPayrollSettingsQuery(tenantId, companyId), cancellationToken));

    private static async Task<IResult> UpdateSettingsAsync(
        Guid tenantId,
        Guid companyId,
        PayrollSettingsRequest request,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new UpdatePayrollSettingsCommand(tenantId, companyId, request, IfMatchOf(httpContext)), cancellationToken));

    private static async Task<IResult> CreateConceptAsync(
        Guid tenantId, Guid companyId, ConceptRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var concept = await dispatcher.SendAsync(new CreateConceptCommand(tenantId, companyId, request), cancellationToken);

        return Results.Created($"/api/v1/tenants/{tenantId}/payroll/companies/{companyId}/concepts/{concept.Id}", concept);
    }

    private static async Task<IResult> UpdateConceptAsync(
        Guid tenantId,
        Guid companyId,
        Guid conceptId,
        ConceptRequest request,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new UpdateConceptCommand(tenantId, companyId, conceptId, request, IfMatchOf(httpContext)), cancellationToken));

    private static async Task<IResult> ArchiveConceptAsync(
        Guid tenantId, Guid companyId, Guid conceptId, bool archive, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new ArchiveConceptCommand(tenantId, companyId, conceptId, archive), cancellationToken));

    // El If-Match llega crudo al caso de uso, que lo exige ya autorizado: parsearlo acá daría 428 a quien opera en otro tenant.
    private static string? IfMatchOf(HttpContext httpContext) => httpContext.Request.Headers.IfMatch.ToString();
}
