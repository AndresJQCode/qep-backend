using BuildingBlocks.Application;
using Modules.Hr.Domain;
using Modules.Tenancy.Application;

namespace Modules.Hr.Application.MedicalExams;

/// <summary>The Excel uploaded to <c>POST /medical-exams/import</c>.</summary>
public sealed record ImportMedicalExamsCommand(Guid TenantId, string FileName, long SizeInBytes, Stream Content)
    : ICommand<MedicalExamImportResultDto>;

/// <summary>
/// The confirmation, all or nothing (spec 2026-11-23 §8): it runs the same analysis as the preview and, with any row in error,
/// answers 422 <c>hr.import.rows_invalid</c> and writes nothing. Otherwise it records every exam and, for each employee that got an
/// exit exam, runs <see cref="MedicalExamRules.Recalculate"/> over all their exams and requirements, so an imported exit exam closes
/// the pending requirement it qualifies for. One audit entry for the whole file, with the count; saved once. A unique violation in a
/// race (someone recorded the same exam after the analysis) is <c>hr.medical_exam.duplicate</c> from <c>HrUnitOfWork</c> and leaves
/// nothing.
/// </summary>
public sealed class ImportMedicalExamsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    MedicalExamImportAnalyzer analyzer,
    IMedicalExamRepository medicalExams,
    IHrUnitOfWork unitOfWork,
    IHrAuditPublisher auditPublisher)
    : ICommandHandler<ImportMedicalExamsCommand, MedicalExamImportResultDto>
{
    public async Task<MedicalExamImportResultDto> HandleAsync(ImportMedicalExamsCommand command, CancellationToken cancellationToken)
    {
        await HrAuthorization.EnsureAsync(
            executionContext,
            tenantModules,
            command.TenantId,
            cancellationToken,
            HrPermissions.EmployeeRead,
            HrPermissions.MedicalRead,
            HrPermissions.MedicalManage);

        var analysis = await analyzer.AnalyzeAsync(
            command.TenantId, command.FileName, command.SizeInBytes, command.Content, cancellationToken);
        if (analysis.Errors.Count > 0)
        {
            throw new HrDomainException(HrErrorCodes.ImportRowsInvalid, "El archivo tiene filas con errores. Corrígelas y vuelve a intentar.");
        }

        var calendar = await tenantClock.GetAsync(command.TenantId, cancellationToken);
        var created = new List<(ParsedMedicalExamRow Row, MedicalExam Exam)>(analysis.ValidRows.Count);
        foreach (var row in analysis.ValidRows)
        {
            var exam = MedicalExam.Create(
                MedicalExamId.New(),
                command.TenantId,
                row.Employee.Id,
                row.Type,
                row.ExamDate,
                calendar.Today,
                row.Provider,
                row.Restrictions,
                row.Recommendations,
                row.RestrictionsUntil,
                calendar.UtcNow);
            medicalExams.Add(exam);
            created.Add((row, exam));
        }

        // Only an exit exam can close a requirement: the other employees need no recalculation.
        var withExit = created
            .Where(entry => entry.Exam.Type == MedicalExamType.Exit)
            .Select(entry => entry.Row.Employee.Id)
            .Distinct()
            .ToList();
        foreach (var employeeId in withExit)
        {
            var exams = await medicalExams.ListExamsAsync(command.TenantId, employeeId, cancellationToken);
            var requirements = await medicalExams.ListRequirementsAsync(command.TenantId, employeeId, cancellationToken);
            var imported = created.Where(entry => entry.Row.Employee.Id == employeeId).Select(entry => entry.Exam);
            MedicalExamRules.Recalculate(requirements, [.. exams, .. imported]);
        }

        HrSettingsSupport.PublishAudit(
            auditPublisher, executionContext, command.TenantId, HrAuditActions.MedicalExamImported, HrAuditActions.Resources.MedicalExam,
            Guid.CreateVersion7(), [$"rows:{created.Count}"], calendar.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new MedicalExamImportResultDto(
            analysis.FileName,
            calendar.UtcNow,
            created.Count,
            created.Select(entry => new MedicalExamImportedRowDto(entry.Row.RowNumber, entry.Exam.Id.Value)).ToList());
    }
}
