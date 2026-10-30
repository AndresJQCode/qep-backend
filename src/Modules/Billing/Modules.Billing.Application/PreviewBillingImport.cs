using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Billing.Application;

/// <summary>El Excel subido a <c>POST /import/preview</c>. <c>Content</c> es el stream del <c>IFormFile</c>, vivo mientras dura el request.</summary>
public sealed record PreviewBillingImportCommand(Guid TenantId, string FileName, long SizeInBytes, Stream Content)
    : ICommand<ImportPreviewDto>;

/// <summary>La vista previa de la importación (§3.2b): el mismo plan que la importación, sin escribir nada.</summary>
public sealed class PreviewBillingImportHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    BillingImportAnalyzer analyzer)
    : ICommandHandler<PreviewBillingImportCommand, ImportPreviewDto>
{
    public async Task<ImportPreviewDto> HandleAsync(PreviewBillingImportCommand command, CancellationToken cancellationToken)
    {
        await BillingAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, BillingPermissions.Manage, cancellationToken);

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
