using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.ServiceContracts.Application;

/// <summary>El Excel subido a <c>POST /import/preview</c>. <c>Content</c> es el stream del <c>IFormFile</c>, vivo mientras dura el request.</summary>
public sealed record PreviewServiceContractsImportCommand(Guid TenantId, string FileName, long SizeInBytes, Stream Content)
    : ICommand<ImportPreviewDto>;

/// <summary>La vista previa de la importación (§3.2b): el mismo plan que la importación, sin escribir nada.</summary>
public sealed class PreviewServiceContractsImportHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ServiceContractsImportAnalyzer analyzer)
    : ICommandHandler<PreviewServiceContractsImportCommand, ImportPreviewDto>
{
    public async Task<ImportPreviewDto> HandleAsync(PreviewServiceContractsImportCommand command, CancellationToken cancellationToken)
    {
        await ServiceContractsAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, ServiceContractsPermissions.Manage, cancellationToken);

        var analysis = await analyzer.AnalyzeAsync(
            command.TenantId, command.FileName, command.SizeInBytes, command.Content, cancellationToken);

        return new ImportPreviewDto(
            analysis.FileName,
            analysis.TotalRows,
            analysis.Plan.Rows.Select(row => row.ToPreview()).ToArray(),
            analysis.Errors,
            analysis.Plan.Summary);
    }
}
