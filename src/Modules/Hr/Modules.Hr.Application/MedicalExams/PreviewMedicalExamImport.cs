using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Hr.Application.MedicalExams;

/// <summary>The Excel uploaded to <c>POST /medical-exams/import/preview</c>. <c>Content</c> is the stream of the <c>IFormFile</c>, alive during the request.</summary>
public sealed record PreviewMedicalExamImportCommand(Guid TenantId, string FileName, long SizeInBytes, Stream Content)
    : ICommand<MedicalExamImportPreviewDto>;

/// <summary>The preview: the same analysis as the confirmation, writing nothing.</summary>
public sealed class PreviewMedicalExamImportHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    MedicalExamImportAnalyzer analyzer)
    : ICommandHandler<PreviewMedicalExamImportCommand, MedicalExamImportPreviewDto>
{
    public async Task<MedicalExamImportPreviewDto> HandleAsync(PreviewMedicalExamImportCommand command, CancellationToken cancellationToken)
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

        var rows = analysis.ValidRows.Select(row => new MedicalExamImportRowPreviewDto(
            row.RowNumber,
            row.Employee.FullName,
            row.Employee.DocumentType.ToString(),
            row.Employee.DocumentNumber,
            row.Type.ToString(),
            row.ExamDate,
            row.Restrictions is not null)).ToList();

        return new MedicalExamImportPreviewDto(
            analysis.FileName, analysis.TotalRows, rows, analysis.Errors, new MedicalExamImportSummaryDto(rows.Count));
    }
}
