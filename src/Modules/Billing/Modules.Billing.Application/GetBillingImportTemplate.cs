using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Billing.Application;

/// <summary>La plantilla de importación (§4): el mismo permiso que importar, como <c>GET /customers/import/template</c>.</summary>
public sealed record GetBillingImportTemplateQuery(Guid TenantId) : IQuery<BillingImportTemplateFile>;

public sealed class GetBillingImportTemplateHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IBillingIdentificationTypes identificationTypes,
    IBillingImportTemplateBuilder templateBuilder)
    : IQueryHandler<GetBillingImportTemplateQuery, BillingImportTemplateFile>
{
    public const string FileName = "plantilla-cobros-recurrentes.xlsx";

    public async Task<BillingImportTemplateFile> HandleAsync(GetBillingImportTemplateQuery query, CancellationToken cancellationToken)
    {
        await BillingAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, BillingPermissions.Manage, cancellationToken);

        var content = templateBuilder.Build(
            identificationTypes.SupportedWireValues,
            Currencies.All.Select(currency => currency.Code).ToArray(),
            cancellationToken);
        return new BillingImportTemplateFile(content, FileName);
    }
}
