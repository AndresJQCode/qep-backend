using Microsoft.EntityFrameworkCore;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;

namespace Modules.Integrations.Infrastructure.Persistence;

internal sealed class IntegrationConnectionRepository(IntegrationsDbContext dbContext) : IIntegrationConnectionRepository
{
    public Task<IntegrationConnection?> FindAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        dbContext.Connections.SingleOrDefaultAsync(
            connection => connection.TenantId == tenantId && connection.Id == connectionId, cancellationToken);

    public async Task<IReadOnlyList<IntegrationConnection>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await dbContext.Connections
            .AsNoTracking()
            .Where(connection => connection.TenantId == tenantId)
            .ToListAsync(cancellationToken);

    public Task<int> CountAsync(Guid tenantId, string providerKey, CancellationToken cancellationToken) =>
        dbContext.Connections.CountAsync(
            connection => connection.TenantId == tenantId && connection.ProviderKey == providerKey, cancellationToken);

    public void Add(IntegrationConnection connection) => dbContext.Connections.Add(connection);

    public void Remove(IntegrationConnection connection) => dbContext.Connections.Remove(connection);
}
