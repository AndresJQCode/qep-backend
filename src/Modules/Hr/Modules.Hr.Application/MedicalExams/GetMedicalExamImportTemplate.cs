using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Hr.Application.MedicalExams;

public sealed record GetMedicalExamImportTemplateQuery(Guid TenantId) : IQuery<MedicalExamImportTemplateFile>;

/// <summary>The template. It does not depend on the tenant configuration; the same permission as importing.</summary>
public sealed class GetMedicalExamImportTemplateHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IMedicalExamImportTemplateBuilder templateBuilder)
    : IQueryHandler<GetMedicalExamImportTemplateQuery, MedicalExamImportTemplateFile>
{
    public async Task<MedicalExamImportTemplateFile> HandleAsync(
        GetMedicalExamImportTemplateQuery query, CancellationToken cancellationToken)
    {
        await HrAuthorization.EnsureAsync(
            executionContext,
            tenantModules,
            query.TenantId,
            cancellationToken,
            HrPermissions.EmployeeRead,
            HrPermissions.MedicalRead,
            HrPermissions.MedicalManage);

        return new MedicalExamImportTemplateFile(templateBuilder.Build(), MedicalExamImportColumns.TemplateFileName);
    }
}
