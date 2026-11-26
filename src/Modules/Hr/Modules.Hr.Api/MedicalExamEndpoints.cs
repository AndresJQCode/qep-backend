using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Hr.Application;
using Modules.Hr.Application.MedicalExams;

namespace Modules.Hr.Api;

/// <summary>
/// The occupational medical exams (spec 2026-11-23 §7). Every route needs <c>hr.employee.read</c> and, on top of it,
/// <c>hr.medical.read</c> (reads) or <c>hr.medical.manage</c> (writes, which also need the read): restrictions and recommendations
/// are health data. Every write answers the whole <see cref="EmployeeMedicalExamsDto"/>, and the screen repaints from it.
/// </summary>
internal static class MedicalExamEndpoints
{
    public static void MapMedicalExams(this RouteGroupBuilder group)
    {
        const string Exams = "/employees/{employeeId:guid}/medical-exams";

        group.MapGet(Exams, GetAsync)
            .RequireAuthorization(HrPermissions.EmployeeRead, HrPermissions.MedicalRead)
            .Produces<EmployeeMedicalExamsDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/medical-exams/overview", OverviewAsync)
            .RequireAuthorization(HrPermissions.EmployeeRead, HrPermissions.MedicalRead)
            .Produces<MedicalExamOverviewDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost(Exams, RecordAsync)
            .RequireAuthorization(HrPermissions.EmployeeRead, HrPermissions.MedicalManage)
            .Accepts<MedicalExamRequest>("application/json")
            .Produces<EmployeeMedicalExamsDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPut($"{Exams}/{{examId:guid}}", UpdateAsync)
            .RequireAuthorization(HrPermissions.EmployeeRead, HrPermissions.MedicalManage)
            .Accepts<MedicalExamRequest>("application/json")
            .Produces<EmployeeMedicalExamsDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapDelete($"{Exams}/{{examId:guid}}", DeleteAsync)
            .RequireAuthorization(HrPermissions.EmployeeRead, HrPermissions.MedicalManage)
            .Produces<EmployeeMedicalExamsDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        // Import (spec 2026-11-23 §8): the exam history from Excel. Antiforgery is off as in the employee import: the CSRF is stopped by
        // X-Qep-Client, not by the form token. The routes are literal and do not clash with `/medical-exams/overview`.
        group.MapGet("/medical-exams/import/template", GetImportTemplateAsync)
            .RequireAuthorization(HrPermissions.EmployeeRead, HrPermissions.MedicalManage)
            .Produces(StatusCodes.Status200OK, contentType: ExcelContentType)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/medical-exams/import/preview", PreviewImportAsync)
            .RequireAuthorization(HrPermissions.EmployeeRead, HrPermissions.MedicalManage)
            .Produces<MedicalExamImportPreviewDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .DisableAntiforgery();

        group.MapPost("/medical-exams/import", ImportAsync)
            .RequireAuthorization(HrPermissions.EmployeeRead, HrPermissions.MedicalManage)
            .Produces<MedicalExamImportResultDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .DisableAntiforgery();

        group.MapPost("/medical-exams/upload-session", OpenUploadSessionAsync)
            .RequireAuthorization(HrPermissions.EmployeeRead, HrPermissions.MedicalManage)
            .Accepts<HrUploadSessionRequest>("application/json")
            .Produces<HrUploadSessionDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPut($"{Exams}/{{examId:guid}}/document", SetDocumentAsync)
            .RequireAuthorization(HrPermissions.EmployeeRead, HrPermissions.MedicalManage)
            .Accepts<MedicalExamDocumentRequest>("application/json")
            .Produces<EmployeeMedicalExamsDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet($"{Exams}/{{examId:guid}}/document", GetDocumentAsync)
            .RequireAuthorization(HrPermissions.EmployeeRead, HrPermissions.MedicalRead)
            .Produces<HrDownloadUrlDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/employees/{employeeId:guid}/exit-exam/not-attended", MarkNotAttendedAsync)
            .RequireAuthorization(HrPermissions.EmployeeRead, HrPermissions.MedicalManage)
            .Accepts<NotAttendedRequest>("application/json")
            .Produces<EmployeeMedicalExamsDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    private static async Task<IResult> GetAsync(
        Guid tenantId, Guid employeeId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetEmployeeMedicalExamsQuery(tenantId, employeeId), cancellationToken));

    // The numbers arrive as text and the validator answers 422 on the field: an automatic binding of `page=abc` would throw a
    // BadHttpRequestException, which ApiExceptionHandler reduces to 500 (same as the employee list).
    private static async Task<IResult> OverviewAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        string? status = null,
        string? search = null,
        string? page = null,
        string? pageSize = null) =>
        Results.Ok(await dispatcher.QueryAsync(
            new GetMedicalExamOverviewQuery(
                tenantId,
                string.IsNullOrWhiteSpace(status) ? null : status.Trim(),
                string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
                string.IsNullOrWhiteSpace(page) ? 1 : int.TryParse(page, out var number) ? number : 0,
                string.IsNullOrWhiteSpace(pageSize)
                    ? GetMedicalExamOverviewQuery.DefaultPageSize
                    : int.TryParse(pageSize, out var size) ? size : 0),
            cancellationToken));

    private static async Task<IResult> RecordAsync(
        Guid tenantId, Guid employeeId, MedicalExamRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new RecordMedicalExamCommand(tenantId, employeeId, request), cancellationToken));

    private static async Task<IResult> UpdateAsync(
        Guid tenantId,
        Guid employeeId,
        Guid examId,
        MedicalExamRequest request,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new UpdateMedicalExamCommand(tenantId, employeeId, examId, request, IfMatchOf(httpContext)), cancellationToken));

    private static async Task<IResult> DeleteAsync(
        Guid tenantId,
        Guid employeeId,
        Guid examId,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new DeleteMedicalExamCommand(tenantId, employeeId, examId, IfMatchOf(httpContext)), cancellationToken));

    private const string ExcelContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static async Task<IResult> GetImportTemplateAsync(
        Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var template = await dispatcher.QueryAsync(new GetMedicalExamImportTemplateQuery(tenantId), cancellationToken);

        return Results.File(template.Content, ExcelContentType, template.FileName);
    }

    // IFormFile nullable: without a file the handler answers 422 hr.import.file_required and not a binding 400.
    private static async Task<IResult> PreviewImportAsync(
        Guid tenantId, IFormFile? file, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        await using var content = file?.OpenReadStream() ?? Stream.Null;

        return Results.Ok(await dispatcher.SendAsync(
            new PreviewMedicalExamImportCommand(tenantId, file?.FileName ?? string.Empty, file?.Length ?? 0, content), cancellationToken));
    }

    private static async Task<IResult> ImportAsync(
        Guid tenantId, IFormFile? file, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        await using var content = file?.OpenReadStream() ?? Stream.Null;

        return Results.Ok(await dispatcher.SendAsync(
            new ImportMedicalExamsCommand(tenantId, file?.FileName ?? string.Empty, file?.Length ?? 0, content), cancellationToken));
    }

    // An absent field is an invalid file: the handler rejects it with hr.file.invalid, never a binding 400.
    private static async Task<IResult> OpenUploadSessionAsync(
        Guid tenantId, HrUploadSessionRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new OpenMedicalExamUploadSessionCommand(
                tenantId, request.FileName ?? string.Empty, request.MimeType ?? string.Empty, request.SizeBytes ?? 0),
            cancellationToken));

    private static async Task<IResult> SetDocumentAsync(
        Guid tenantId,
        Guid employeeId,
        Guid examId,
        MedicalExamDocumentRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new SetMedicalExamDocumentCommand(tenantId, employeeId, examId, request.FileId), cancellationToken));

    private static async Task<IResult> GetDocumentAsync(
        Guid tenantId, Guid employeeId, Guid examId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetMedicalExamDocumentQuery(tenantId, employeeId, examId), cancellationToken));

    private static async Task<IResult> MarkNotAttendedAsync(
        Guid tenantId, Guid employeeId, NotAttendedRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new MarkExitNotAttendedCommand(tenantId, employeeId, request), cancellationToken));

    // The If-Match arrives raw to the use case, which reads it once authorized: parsing it here would answer 428 to someone who
    // operates in another tenant.
    private static string? IfMatchOf(HttpContext httpContext) => httpContext.Request.Headers.IfMatch.ToString();
}
