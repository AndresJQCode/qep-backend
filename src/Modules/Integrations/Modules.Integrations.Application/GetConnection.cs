using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Integrations.Application;

public sealed record GetConnectionQuery(Guid TenantId, Guid ConnectionId) : IQuery<ConnectionResponse>;

public sealed class GetConnectionHandler(
    IIntegrationProviderCatalog catalog,
    IIntegrationConnectionRepository repository,
    ITenantModules tenantModules,
    ISecretProtector protector,
    IConnectionAuthorNames authorNames,
    IExecutionContext executionContext)
    : IQueryHandler<GetConnectionQuery, ConnectionResponse>
{
    public async Task<ConnectionResponse> HandleAsync(GetConnectionQuery query, CancellationToken cancellationToken)
    {
        IntegrationsAuthorization.EnsureAuthorized(executionContext, query.TenantId, IntegrationsPermissions.ConnectionRead);
        var (connection, provider) = await ConnectionLoader.LoadVisibleAsync(
            repository, catalog, tenantModules, query.TenantId, query.ConnectionId, cancellationToken);
        return await ConnectionMapping.ToResponseAsync(connection, provider, protector, authorNames, cancellationToken);
    }
}
