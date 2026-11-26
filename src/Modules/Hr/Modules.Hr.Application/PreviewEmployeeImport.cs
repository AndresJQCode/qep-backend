using BuildingBlocks.Application;
using Modules.Hr.Domain;
using Modules.Tenancy.Application;

namespace Modules.Hr.Application;

/// <summary>El Excel subido a <c>POST /employees/import/preview</c>. <c>Content</c> es el stream del <c>IFormFile</c>, vivo mientras dura el request.</summary>
public sealed record PreviewEmployeeImportCommand(Guid TenantId, string FileName, long SizeInBytes, Stream Content)
    : ICommand<EmployeeImportPreviewDto>;

/// <summary>La vista previa: el mismo análisis que la confirmación, sin escribir nada.</summary>
public sealed class PreviewEmployeeImportHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    EmployeeImportAnalyzer analyzer)
    : ICommandHandler<PreviewEmployeeImportCommand, EmployeeImportPreviewDto>
{
    public async Task<EmployeeImportPreviewDto> HandleAsync(PreviewEmployeeImportCommand command, CancellationToken cancellationToken)
    {
        await HrAuthorization.EnsureAsync(
            executionContext,
            tenantModules,
            command.TenantId,
            cancellationToken,
            HrPermissions.EmployeeRead,
            HrPermissions.EmployeeManage,
            HrPermissions.ContractRead,
            HrPermissions.ContractManage);

        var analysis = await analyzer.AnalyzeAsync(
            command.TenantId, command.FileName, command.SizeInBytes, command.Content, cancellationToken);

        var preview = analysis.ValidRows.Select(row => new EmployeeImportRowPreviewDto(
            row.RowNumber,
            row.Profile.FirstName,
            row.Profile.LastName,
            row.Profile.DocumentType.ToString(),
            row.Profile.DocumentNumber,
            row.CompanyName,
            row.Terms.ContractType.ToString(),
            row.Terms.Duration.ToString(),
            analysis.Settings.PositionName(row.PositionId) ?? string.Empty,
            analysis.Settings.WorkplaceLabel(row.Workplace),
            row.Salary,
            row.Terms.StartDate,
            row.RetirementDate ?? row.Terms.EndDate,
            row.RetirementDate is null ? nameof(ContractStatus.Active) : nameof(ContractStatus.Terminated)))
            .ToList();

        return new EmployeeImportPreviewDto(
            analysis.FileName,
            analysis.TotalRows,
            preview,
            analysis.Errors,
            analysis.Warnings,
            new EmployeeImportSummaryDto(
                preview.Count,
                analysis.ValidRows.Count(row => row.RetirementDate is null),
                analysis.ValidRows.Count(row => row.RetirementDate is not null)));
    }
}
