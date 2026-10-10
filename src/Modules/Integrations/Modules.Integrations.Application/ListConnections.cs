using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Integrations.Application;

public sealed record ListConnectionsQuery(Guid TenantId) : IQuery<ConnectionsResponse>;

/// <summary>
/// Spec 2026-10-08, <c>GET /connections</c>: las del tenant cuyo proveedor es visible, por proveedor y
/// nombre (P20). Los nombres de los autores se piden en una sola llamada.
/// </summary>
public sealed class ListConnectionsHandler(
    IIntegrationProviderCatalog catalog,
    IIntegrationConnectionRepository repository,
    ITenantModules tenantModules,
    IMetaAppSettings metaApp,
    ISecretProtector protector,
    IConnectionAuthorNames authorNames,
    IExecutionContext executionContext)
    : IQueryHandler<ListConnectionsQuery, ConnectionsResponse>
{
    public async Task<ConnectionsResponse> HandleAsync(ListConnectionsQuery query, CancellationToken cancellationToken)
    {
        IntegrationsAuthorization.EnsureAuthorized(executionContext, query.TenantId, IntegrationsPermissions.ConnectionRead);

        var visible = (await ProviderVisibility.VisibleAsync(catalog, tenantModules, metaApp, query.TenantId, cancellationToken))
            .ToDictionary(provider => provider.Key, StringComparer.Ordinal);
        var connections = (await repository.ListAsync(query.TenantId, cancellationToken))
            .Where(connection => visible.ContainsKey(connection.ProviderKey))
            .OrderBy(connection => connection.ProviderKey, StringComparer.Ordinal)
            .ThenBy(connection => connection.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(connection => connection.Id)
            .ToArray();
        var names = await authorNames.FindAsync(
            query.TenantId, connections.Select(connection => connection.CreatedBy).Distinct().ToArray(), cancellationToken);

        return new ConnectionsResponse(connections
            .Select(connection => ConnectionMapping.ToResponse(connection, visible[connection.ProviderKey], protector, names))
            .ToArray());
    }
}
