using BuildingBlocks.Application;
using Modules.Hr.Domain;
using Modules.Tenancy.Application;

namespace Modules.Hr.Application;

public sealed record GetEmployeeImportTemplateQuery(Guid TenantId) : IQuery<EmployeeImportTemplateFile>;

/// <summary>
/// La plantilla de importación: depende de la configuración del tenant (empresas activas, cargos, áreas y centros no archivados), así
/// que sin configuración lista responde <c>hr.settings.setup_incomplete</c> igual que el alta. El mismo permiso que importar.
/// </summary>
public sealed class GetEmployeeImportTemplateHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    HrSetup setup,
    EmployeeImportCatalogs catalogs,
    IEmployeeImportTemplateBuilder templateBuilder)
    : IQueryHandler<GetEmployeeImportTemplateQuery, EmployeeImportTemplateFile>
{
    public async Task<EmployeeImportTemplateFile> HandleAsync(GetEmployeeImportTemplateQuery query, CancellationToken cancellationToken)
    {
        await HrAuthorization.EnsureAsync(
            executionContext,
            tenantModules,
            query.TenantId,
            cancellationToken,
            HrPermissions.EmployeeRead,
            HrPermissions.EmployeeManage,
            HrPermissions.ContractRead,
            HrPermissions.ContractManage);

        if (!await setup.IsCompleteAsync(query.TenantId, cancellationToken))
        {
            throw new HrDomainException(
                HrErrorCodes.SetupIncomplete, "Configura una empresa activa con sede y centro de trabajo, y un cargo, antes de importar.");
        }

        var content = templateBuilder.Build(
            await catalogs.LoadSettingsAsync(query.TenantId, cancellationToken),
            await catalogs.LoadCountriesAsync(cancellationToken));
        return new EmployeeImportTemplateFile(content, HrImportColumns.TemplateFileName);
    }
}
