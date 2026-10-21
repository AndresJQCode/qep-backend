using BuildingBlocks.Application;
using Modules.Integrations.Application.Shopify;
using Modules.Tenancy.Application;

namespace Modules.Integrations.Application;

public sealed record GetIntegrationsCatalogQuery(Guid TenantId) : IQuery<IntegrationsCatalogResponse>;

/// <summary>Spec 2026-10-08, <c>GET /catalog</c>: sólo los proveedores visibles, en el orden del catálogo. Plan
/// 2026-10-21: uno visible que exige habilitación y no la tiene sale sólo si el tenant tiene conexiones suyas, con
/// <c>platformEnabled = false</c>, para que la pantalla las agrupe; sin conexiones, no se ofrece.</summary>
public sealed class GetIntegrationsCatalogHandler(
    IIntegrationProviderCatalog catalog,
    IIntegrationConnectionRepository repository,
    ITenantModules tenantModules,
    ITenantProviders tenantProviders,
    IMetaAppSettings metaApp,
    IInstagramAppSettings instagramApp,
    IShopifyAppSettings shopifyApp,
    IExecutionContext executionContext)
    : IQueryHandler<GetIntegrationsCatalogQuery, IntegrationsCatalogResponse>
{
    public async Task<IntegrationsCatalogResponse> HandleAsync(
        GetIntegrationsCatalogQuery query, CancellationToken cancellationToken)
    {
        IntegrationsAuthorization.EnsureAuthorized(executionContext, query.TenantId, IntegrationsPermissions.ConnectionRead);

        var visible = await ProviderVisibility.VisibleAsync(catalog, tenantModules, metaApp, instagramApp, shopifyApp, query.TenantId, cancellationToken);
        var enabled = await tenantProviders.FindEnabledAsync(query.TenantId, cancellationToken);
        var connections = await repository.ListAsync(query.TenantId, cancellationToken);

        return new IntegrationsCatalogResponse(visible
            .Select(provider => (
                Provider: provider,
                Count: connections.Count(connection => string.Equals(connection.ProviderKey, provider.Key, StringComparison.Ordinal)),
                Enabled: provider.IsEnabledFor(enabled)))
            .Where(entry => entry.Enabled || entry.Count > 0)
            .Select(entry => ConnectionMapping.ToProvider(entry.Provider, entry.Count, entry.Enabled, metaApp, instagramApp))
            .ToArray());
    }
}
