using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Store.Application;
using Modules.Store.Domain;

namespace Modules.Store.Infrastructure.Persistence;

internal sealed class StoreDomainRepository(StoreDbContext dbContext, IClock clock) : IStoreDomainRepository
{
    public Task<StoreDomain?> FindByHostAsync(string hostname, CancellationToken cancellationToken) =>
        dbContext.Domains
            .AsNoTracking()
            .FirstOrDefaultAsync(value => value.Hostname == hostname, cancellationToken);

    public async Task ReplaceSubdomainAsync(Guid tenantId, string? hostname, CancellationToken cancellationToken)
    {
        var current = await dbContext.Domains
            .Where(value => value.TenantId == tenantId && value.Kind == StoreDomainKind.Subdomain)
            .ToListAsync(cancellationToken);

        // Si el hostname no cambió no se toca nada: borrar e insertar la misma clave en un guardado es un UPDATE inútil.
        if (current.Count == 1 && string.Equals(current[0].Hostname, hostname, StringComparison.Ordinal))
        {
            return;
        }

        dbContext.Domains.RemoveRange(current);

        if (hostname is null)
        {
            return;
        }

        // El puerto recibe el hostname ya armado; el dominio lo arma de slug + dominio raíz, así que se parte en el primer punto.
        var dot = hostname.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0 || dot == hostname.Length - 1)
        {
            throw new ArgumentException("The hostname must be a slug followed by a root domain.", nameof(hostname));
        }

        dbContext.Domains.Add(StoreDomain.CreateSubdomain(tenantId, hostname[..dot], hostname[(dot + 1)..], clock.UtcNow));
    }
}
