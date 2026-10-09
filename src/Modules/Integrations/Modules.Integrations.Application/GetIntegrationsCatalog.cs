using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Integrations.Application;

public sealed record GetIntegrationsCatalogQuery(Guid TenantId) : IQuery<IntegrationsCatalogResponse>;

/// <summary>Spec 2026-10-08, <c>GET /catalog</c>: sólo los proveedores visibles, en el orden del catálogo.</summary>
public sealed class GetIntegrationsCatalogHandler(
    IIntegrationProviderCatalog catalog,
    IIntegrationConnectionRepository repository,
    ITenantModules tenantModules,
    IExecutionContext executionContext)
    : IQueryHandler<GetIntegrationsCatalogQuery, IntegrationsCatalogResponse>
{
    public async Task<IntegrationsCatalogResponse> HandleAsync(
        GetIntegrationsCatalogQuery query, CancellationToken cancellationToken)
    {
        IntegrationsAuthorization.EnsureAuthorized(executionContext, query.TenantId, IntegrationsPermissions.ConnectionRead);

        var visible = await ProviderVisibility.VisibleAsync(catalog, tenantModules, query.TenantId, cancellationToken);
        var connections = await repository.ListAsync(query.TenantId, cancellationToken);

        return new IntegrationsCatalogResponse(visible
            .Select(provider => ConnectionMapping.ToProvider(
                provider,
                connections.Count(connection => string.Equals(connection.ProviderKey, provider.Key, StringComparison.Ordinal))))
            .ToArray());
    }
}
