using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Hr.Application;
using Modules.Hr.Application.BirthdayNotices;
using Modules.Hr.Application.MedicalExams;

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
        MapEmployees(group);
        MapContracts(group);
        group.MapMedicalExams();

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

        // Birthday notices (spec 2026-11-26 §5.1): who receives the daily digest. The PUT is an upsert: the first one carries no
        // If-Match, the following ones must.
        group.MapGet("/birthday-notices", GetBirthdayNoticesAsync)
            .RequireAuthorization(HrPermissions.EmployeeRead)
            .Produces<BirthdayNoticesDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPut("/birthday-notices", SaveBirthdayNoticesAsync)
            .RequireAuthorization(HrPermissions.SettingsManage)
            .Accepts<BirthdayNoticeRequest>("application/json")
            .Produces<BirthdayNoticesDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

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

        // Spec 2026-11-23 §5.2: ruta propia, así un frontend viejo que sólo manda el nombre no borra la periodicidad.
        group.MapPut("/positions/{id:guid}/exam-frequency", SetExamFrequencyAsync)
            .RequireAuthorization(HrPermissions.SettingsManage)
            .Accepts<ExamFrequencyRequest>("application/json")
            .Produces<PositionDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
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

    // Empleados. La política de cada ruta es el permiso en negrita de la tabla del plan; el handler revalida la lista completa (D-1).
    // Las rutas de la importación (`/employees/import/*`, Tarea 10) se registran antes que `/employees/{employeeId:guid}`.
    private static void MapEmployees(RouteGroupBuilder group)
    {
        group.MapGet("/employees", ListEmployeesAsync)
            .RequireAuthorization(HrPermissions.EmployeeRead)
            .Produces<EmployeePageDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/employees", CreateEmployeeAsync)
            .RequireAuthorization(HrPermissions.ContractManage)
            .Accepts<CreateEmployeeRequest>("application/json")
            .Produces<EmployeeDetailDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Importación por Excel: las tres rutas van antes de `/employees/{employeeId:guid}`. Todo con `hr.contract.manage` (y el handler
        // exige también los otros tres de empleados y contratos), también la plantilla. Antiforgery apagado como en Contratos de
        // servicio: el CSRF lo frena X-Qep-Client, no el token de formulario.
        group.MapGet("/employees/import/template", GetImportTemplateAsync)
            .RequireAuthorization(HrPermissions.ContractManage)
            .Produces(StatusCodes.Status200OK, contentType: ExcelContentType)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/employees/import/preview", PreviewImportAsync)
            .RequireAuthorization(HrPermissions.ContractManage)
            .Produces<EmployeeImportPreviewDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .DisableAntiforgery();

        group.MapPost("/employees/import", ImportAsync)
            .RequireAuthorization(HrPermissions.ContractManage)
            .Produces<EmployeeImportResultDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .DisableAntiforgery();

        group.MapGet("/employees/{employeeId:guid}", GetEmployeeAsync)
            .RequireAuthorization(HrPermissions.EmployeeRead)
            .Produces<EmployeeDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/employees/{employeeId:guid}", UpdateEmployeeAsync)
            .RequireAuthorization(HrPermissions.EmployeeManage)
            .Accepts<EmployeeRequest>("application/json")
            .Produces<EmployeeDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapPut("/employees/{employeeId:guid}/payment", UpdateEmployeePaymentAsync)
            .RequireAuthorization(HrPermissions.ContractManage)
            .Accepts<PaymentRequest>("application/json")
            .Produces<EmployeeDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapPost("/employees/{employeeId:guid}/deactivate", DeactivateEmployeeAsync)
            .RequireAuthorization(HrPermissions.EmployeeManage)
            .Accepts<DeactivateRequest>("application/json")
            .Produces<EmployeeDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapDelete("/employees/{employeeId:guid}", DeleteEmployeeAsync)
            .RequireAuthorization(HrPermissions.EmployeeManage)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/employees/{employeeId:guid}/activity", ListEmployeeActivityAsync)
            .RequireAuthorization(HrPermissions.EmployeeRead)
            .Produces<ActivityPageDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/members-available", ListAvailableMembersAsync)
            .RequireAuthorization(HrPermissions.EmployeeManage)
            .Produces<IReadOnlyList<MemberRefDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden);
    }

    // El contrato del empleado: toda ruta es `hr.contract.manage` (y el handler exige también `employee.read` y `contract.read`). Sólo
    // el `PUT` de salario lleva `If-Match`; en las demás, una escritura concurrente igual responde 412 porque la versión del empleado es
    // token de concurrencia.
    private static void MapContracts(RouteGroupBuilder group)
    {
        const string Contract = "/employees/{employeeId:guid}/contracts/{contractId:guid}";

        group.MapPost("/employees/{employeeId:guid}/reincorporate", ReincorporateAsync)
            .RequireAuthorization(HrPermissions.ContractManage)
            .Accepts<NewContractRequest>("application/json")
            .ProducesContractWrite();

        group.MapPost($"{Contract}/salaries", AddSalaryAsync)
            .RequireAuthorization(HrPermissions.ContractManage)
            .Accepts<SalaryRequest>("application/json")
            .ProducesContractWrite();

        group.MapPut($"{Contract}/salaries/{{salaryId:guid}}", RewriteSalaryAsync)
            .RequireAuthorization(HrPermissions.ContractManage)
            .Accepts<SalaryRewriteRequest>("application/json")
            .ProducesContractWrite()
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapPost($"{Contract}/assignments", AddAssignmentAsync)
            .RequireAuthorization(HrPermissions.ContractManage)
            .Accepts<AssignmentRequest>("application/json")
            .ProducesContractWrite();

        group.MapPost($"{Contract}/renewals", RenewAsync)
            .RequireAuthorization(HrPermissions.ContractManage)
            .Accepts<RenewalRequest>("application/json")
            .ProducesContractWrite();

        group.MapPost($"{Contract}/convert-to-indefinite", ConvertToIndefiniteAsync)
            .RequireAuthorization(HrPermissions.ContractManage)
            .ProducesContractWrite();

        group.MapPost($"{Contract}/conversion", ChangeTypeAsync)
            .RequireAuthorization(HrPermissions.ContractManage)
            .Accepts<NewContractRequest>("application/json")
            .ProducesContractWrite();

        group.MapPost($"{Contract}/terminate", TerminateAsync)
            .RequireAuthorization(HrPermissions.ContractManage)
            .Accepts<TerminateRequest>("application/json")
            .ProducesContractWrite();

        group.MapPut($"{Contract}/document", (Guid tenantId, Guid employeeId, Guid contractId, DocumentRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                SetDocumentAsync(tenantId, employeeId, contractId, HrDocumentTarget.Contract, null, request, dispatcher, cancellationToken))
            .RequireAuthorization(HrPermissions.ContractManage)
            .Accepts<DocumentRequest>("application/json")
            .ProducesContractWrite();

        group.MapPut($"{Contract}/salaries/{{recordId:guid}}/document", (Guid tenantId, Guid employeeId, Guid contractId, Guid recordId, DocumentRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                SetDocumentAsync(tenantId, employeeId, contractId, HrDocumentTarget.Salary, recordId, request, dispatcher, cancellationToken))
            .RequireAuthorization(HrPermissions.ContractManage)
            .Accepts<DocumentRequest>("application/json")
            .ProducesContractWrite();

        group.MapPut($"{Contract}/assignments/{{recordId:guid}}/document", (Guid tenantId, Guid employeeId, Guid contractId, Guid recordId, DocumentRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                SetDocumentAsync(tenantId, employeeId, contractId, HrDocumentTarget.Assignment, recordId, request, dispatcher, cancellationToken))
            .RequireAuthorization(HrPermissions.ContractManage)
            .Accepts<DocumentRequest>("application/json")
            .ProducesContractWrite();

        group.MapPut($"{Contract}/renewals/{{recordId:guid}}/document", (Guid tenantId, Guid employeeId, Guid contractId, Guid recordId, DocumentRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                SetDocumentAsync(tenantId, employeeId, contractId, HrDocumentTarget.Renewal, recordId, request, dispatcher, cancellationToken))
            .RequireAuthorization(HrPermissions.ContractManage)
            .Accepts<DocumentRequest>("application/json")
            .ProducesContractWrite();
    }

    private static RouteHandlerBuilder ProducesContractWrite(this RouteHandlerBuilder builder) => builder
        .Produces<EmployeeDetailDto>()
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status412PreconditionFailed)
        .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

    private static async Task<IResult> ReincorporateAsync(
        Guid tenantId, Guid employeeId, NewContractRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new ReincorporateEmployeeCommand(tenantId, employeeId, request), cancellationToken));

    private static async Task<IResult> AddSalaryAsync(
        Guid tenantId, Guid employeeId, Guid contractId, SalaryRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new AddSalaryCommand(tenantId, employeeId, contractId, request), cancellationToken));

    private static async Task<IResult> RewriteSalaryAsync(
        Guid tenantId,
        Guid employeeId,
        Guid contractId,
        Guid salaryId,
        SalaryRewriteRequest request,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new RewriteSalaryCommand(tenantId, employeeId, contractId, salaryId, request, IfMatchOf(httpContext)), cancellationToken));

    private static async Task<IResult> AddAssignmentAsync(
        Guid tenantId, Guid employeeId, Guid contractId, AssignmentRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new AddAssignmentCommand(tenantId, employeeId, contractId, request), cancellationToken));

    private static async Task<IResult> RenewAsync(
        Guid tenantId, Guid employeeId, Guid contractId, RenewalRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new RenewContractCommand(tenantId, employeeId, contractId, request), cancellationToken));

    private static async Task<IResult> ConvertToIndefiniteAsync(
        Guid tenantId, Guid employeeId, Guid contractId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new ConvertContractToIndefiniteCommand(tenantId, employeeId, contractId), cancellationToken));

    private static async Task<IResult> ChangeTypeAsync(
        Guid tenantId, Guid employeeId, Guid contractId, NewContractRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new ChangeContractTypeCommand(tenantId, employeeId, contractId, request), cancellationToken));

    private static async Task<IResult> TerminateAsync(
        Guid tenantId, Guid employeeId, Guid contractId, TerminateRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new TerminateContractCommand(tenantId, employeeId, contractId, request), cancellationToken));

    private static async Task<IResult> SetDocumentAsync(
        Guid tenantId,
        Guid employeeId,
        Guid contractId,
        HrDocumentTarget target,
        Guid? recordId,
        DocumentRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new SetHrDocumentCommand(tenantId, employeeId, contractId, target, recordId, request.FileId), cancellationToken));

    // Los números llegan como texto y el handler los valida, ya autorizado: un `page=abc` que dejara al binding automático lanzaría una
    // BadHttpRequestException, y ApiExceptionHandler la reduce a 500. Lo que no se puede leer se pasa como 0 para que el validador
    // responda 422 sobre el campo.
    private static async Task<IResult> ListEmployeesAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        string? search = null,
        string? status = null,
        string? expiringSoon = null,
        string? companyId = null,
        string? page = null,
        string? pageSize = null)
    {
        var result = await dispatcher.QueryAsync(
            new ListEmployeesQuery(
                tenantId,
                string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
                string.IsNullOrWhiteSpace(status) ? null : status.Trim(),
                string.Equals(expiringSoon, "true", StringComparison.OrdinalIgnoreCase),
                string.IsNullOrWhiteSpace(companyId) ? null : Guid.TryParse(companyId, out var company) ? company : Guid.Empty,
                PageNumberOf(page),
                PageSizeOf(pageSize)),
            cancellationToken);

        return Results.Ok(result);
    }

    private static int PageNumberOf(string? page) =>
        string.IsNullOrWhiteSpace(page) ? 1 : int.TryParse(page, out var number) ? number : 0;

    private static int PageSizeOf(string? pageSize) =>
        string.IsNullOrWhiteSpace(pageSize) ? ListEmployeesQuery.DefaultPageSize : int.TryParse(pageSize, out var size) ? size : 0;

    private static async Task<IResult> CreateEmployeeAsync(
        Guid tenantId, CreateEmployeeRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var detail = await dispatcher.SendAsync(new CreateEmployeeCommand(tenantId, request), cancellationToken);

        return Results.Created($"/api/v1/tenants/{tenantId}/hr/employees/{detail.Employee.Id}", detail);
    }

    private const string ExcelContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static async Task<IResult> GetImportTemplateAsync(
        Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var template = await dispatcher.QueryAsync(new GetEmployeeImportTemplateQuery(tenantId), cancellationToken);

        return Results.File(template.Content, ExcelContentType, template.FileName);
    }

    // IFormFile anulable: sin archivo el handler responde 422 hr.import.file_required y no un 400 del binding.
    private static async Task<IResult> PreviewImportAsync(
        Guid tenantId, IFormFile? file, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        await using var content = file?.OpenReadStream() ?? Stream.Null;
        var preview = await dispatcher.SendAsync(
            new PreviewEmployeeImportCommand(tenantId, file?.FileName ?? string.Empty, file?.Length ?? 0, content), cancellationToken);

        return Results.Ok(preview);
    }

    private static async Task<IResult> ImportAsync(
        Guid tenantId, IFormFile? file, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        await using var content = file?.OpenReadStream() ?? Stream.Null;
        var result = await dispatcher.SendAsync(
            new ImportEmployeesCommand(tenantId, file?.FileName ?? string.Empty, file?.Length ?? 0, content), cancellationToken);

        return Results.Ok(result);
    }

    private static async Task<IResult> GetEmployeeAsync(
        Guid tenantId, Guid employeeId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetEmployeeQuery(tenantId, employeeId), cancellationToken));

    private static async Task<IResult> UpdateEmployeeAsync(
        Guid tenantId,
        Guid employeeId,
        EmployeeRequest request,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new UpdateEmployeeCommand(tenantId, employeeId, request, IfMatchOf(httpContext)), cancellationToken));

    private static async Task<IResult> UpdateEmployeePaymentAsync(
        Guid tenantId,
        Guid employeeId,
        PaymentRequest request,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new UpdateEmployeePaymentCommand(tenantId, employeeId, request, IfMatchOf(httpContext)), cancellationToken));

    private static async Task<IResult> DeactivateEmployeeAsync(
        Guid tenantId, Guid employeeId, DeactivateRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new DeactivateEmployeeCommand(tenantId, employeeId, request), cancellationToken));

    private static async Task<IResult> DeleteEmployeeAsync(
        Guid tenantId, Guid employeeId, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(new DeleteEmployeeCommand(tenantId, employeeId), cancellationToken);

        return Results.NoContent();
    }

    private static async Task<IResult> ListEmployeeActivityAsync(
        Guid tenantId,
        Guid employeeId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        string? page = null,
        string? pageSize = null) =>
        Results.Ok(await dispatcher.QueryAsync(
            new ListEmployeeActivityQuery(tenantId, employeeId, PageNumberOf(page), PageSizeOf(pageSize)), cancellationToken));

    private static async Task<IResult> ListAvailableMembersAsync(
        Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListAvailableMembersQuery(tenantId), cancellationToken));

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

    private static async Task<IResult> GetBirthdayNoticesAsync(
        Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var notices = await dispatcher.QueryAsync(new GetBirthdayNoticesQuery(tenantId), cancellationToken);

        return Results.Ok(notices);
    }

    private static async Task<IResult> SaveBirthdayNoticesAsync(
        Guid tenantId,
        BirthdayNoticeRequest request,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var notices = await dispatcher.SendAsync(
            new SaveBirthdayNoticesCommand(tenantId, request, IfMatchOf(httpContext)), cancellationToken);

        return Results.Ok(notices);
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

    private static async Task<IResult> SetExamFrequencyAsync(
        Guid tenantId,
        Guid id,
        ExamFrequencyRequest request,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var position = await dispatcher.SendAsync(
            new SetPositionExamFrequencyCommand(tenantId, id, request, IfMatchOf(httpContext)), cancellationToken);

        return Results.Ok(position);
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
