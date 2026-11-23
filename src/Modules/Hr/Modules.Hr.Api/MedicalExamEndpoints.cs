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

    private static async Task<IResult> MarkNotAttendedAsync(
        Guid tenantId, Guid employeeId, NotAttendedRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new MarkExitNotAttendedCommand(tenantId, employeeId, request), cancellationToken));

    // The If-Match arrives raw to the use case, which reads it once authorized: parsing it here would answer 428 to someone who
    // operates in another tenant.
    private static string? IfMatchOf(HttpContext httpContext) => httpContext.Request.Headers.IfMatch.ToString();
}
