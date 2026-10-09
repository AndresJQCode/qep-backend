using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;

namespace Modules.Integrations.Infrastructure.Persistence;

/// <summary>Join con <c>connections</c> para traer el estado: el worker decide por él (§8.2).</summary>
internal sealed class ConnectionRoutes(IntegrationsDbContext dbContext) : IConnectionRoutes
{
    public Task<ConnectionRoute?> FindAsync(string providerKey, string externalId, CancellationToken cancellationToken) =>
        Query(route => route.ProviderKey == providerKey && route.ExternalId == externalId)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<ConnectionRoute>> FindByAccountAsync(string providerKey, string accountId, CancellationToken cancellationToken) =>
        await Query(route => route.ProviderKey == providerKey && route.AccountId == accountId)
            .ToListAsync(cancellationToken);

    // El filtro va sobre la ruta, antes del join: EF no traduce un Where sobre los miembros de una tupla
    // o de un record posicional armado en el resultSelector.
    private IQueryable<ConnectionRoute> Query(Expression<Func<IntegrationConnectionRoute, bool>> filter) =>
        dbContext.Routes.AsNoTracking()
            .Where(filter)
            .Join(dbContext.Connections.AsNoTracking(), route => route.ConnectionId, connection => connection.Id,
                (route, connection) => new ConnectionRoute(route.TenantId, route.ConnectionId, connection.Status));
}
