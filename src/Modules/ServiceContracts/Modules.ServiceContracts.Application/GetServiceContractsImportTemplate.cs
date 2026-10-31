using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.ServiceContracts.Application;

/// <summary>La plantilla de importación (§4): el mismo permiso que importar, como <c>GET /customers/import/template</c>.</summary>
public sealed record GetServiceContractsImportTemplateQuery(Guid TenantId) : IQuery<ServiceContractsImportTemplateFile>;

public sealed class GetServiceContractsImportTemplateHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IServiceContractsIdentificationTypes identificationTypes,
    IServiceContractsImportTemplateBuilder templateBuilder)
    : IQueryHandler<GetServiceContractsImportTemplateQuery, ServiceContractsImportTemplateFile>
{
    public const string FileName = "plantilla-cobros-recurrentes.xlsx";

    public async Task<ServiceContractsImportTemplateFile> HandleAsync(GetServiceContractsImportTemplateQuery query, CancellationToken cancellationToken)
    {
        await ServiceContractsAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, ServiceContractsPermissions.Manage, cancellationToken);

        var content = templateBuilder.Build(
            identificationTypes.SupportedWireValues,
            Currencies.All.Select(currency => currency.Code).ToArray(),
            cancellationToken);
        return new ServiceContractsImportTemplateFile(content, FileName);
    }
}
