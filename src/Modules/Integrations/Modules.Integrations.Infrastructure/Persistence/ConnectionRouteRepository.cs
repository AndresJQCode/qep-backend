using Microsoft.EntityFrameworkCore;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;

namespace Modules.Integrations.Infrastructure.Persistence;

internal sealed class ConnectionRouteRepository(IntegrationsDbContext dbContext) : IConnectionRouteRepository
{
    public void Add(IntegrationConnectionRoute route) => dbContext.Routes.Add(route);

    public Task<bool> ExistsAsync(string providerKey, string externalId, CancellationToken cancellationToken) =>
        dbContext.Routes.AnyAsync(route => route.ProviderKey == providerKey && route.ExternalId == externalId, cancellationToken);
}
