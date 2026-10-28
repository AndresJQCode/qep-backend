using BuildingBlocks.Application;
using Modules.Integrations.Application.Shopify;
using Modules.Tenancy.Application;

namespace Modules.Integrations.Application;

public sealed record ListConnectionsQuery(Guid TenantId) : IQuery<ConnectionsResponse>;

/// <summary>
/// Spec 2026-10-08, <c>GET /connections</c>: las del tenant cuyo proveedor es visible, por proveedor y
/// nombre (P20). Los nombres de los autores se piden en una sola llamada. Plan 2026-10-21: las de un proveedor
/// deshabilitado siguen saliendo, con <c>providerEnabled = false</c>.
/// </summary>
public sealed class ListConnectionsHandler(
    IIntegrationProviderCatalog catalog,
    IIntegrationConnectionRepository repository,
    ITenantModules tenantModules,
    ITenantProviders tenantProviders,
    IMetaAppSettings metaApp,
    IInstagramAppSettings instagramApp,
    IShopifyAppSettings shopifyApp,
    ISecretProtector protector,
    IConnectionAuthorNames authorNames,
    IExecutionContext executionContext)
    : IQueryHandler<ListConnectionsQuery, ConnectionsResponse>
{
    public async Task<ConnectionsResponse> HandleAsync(ListConnectionsQuery query, CancellationToken cancellationToken)
    {
        IntegrationsAuthorization.EnsureAuthorized(executionContext, query.TenantId, IntegrationsPermissions.ConnectionRead);

        var visible = (await ProviderVisibility.VisibleAsync(catalog, tenantModules, metaApp, instagramApp, shopifyApp, query.TenantId, cancellationToken))
            .ToDictionary(provider => provider.Key, StringComparer.Ordinal);
        var connections = (await repository.ListAsync(query.TenantId, cancellationToken))
            .Where(connection => visible.ContainsKey(connection.ProviderKey))
            .OrderBy(connection => connection.ProviderKey, StringComparer.Ordinal)
            .ThenBy(connection => connection.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(connection => connection.Id)
            .ToArray();
        var enabled = await tenantProviders.FindEnabledAsync(query.TenantId, cancellationToken);
        var names = await authorNames.FindAsync(
            query.TenantId, connections.Select(connection => connection.CreatedBy).Distinct().ToArray(), cancellationToken);

        return new ConnectionsResponse(connections
            .Select(connection =>
            {
                var provider = visible[connection.ProviderKey];
                return ConnectionMapping.ToResponse(connection, provider, protector, names, provider.IsEnabledFor(enabled));
            })
            .ToArray());
    }
}
