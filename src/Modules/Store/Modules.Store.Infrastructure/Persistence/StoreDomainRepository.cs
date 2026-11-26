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

    public async Task ReplaceSubdomainAsync(Guid tenantId, string? slug, string rootDomain, CancellationToken cancellationToken)
    {
        var current = await dbContext.Domains
            .Where(value => value.TenantId == tenantId && value.Kind == StoreDomainKind.Subdomain)
            .ToListAsync(cancellationToken);

        // El dominio arma el hostname (y lo valida) de slug + dominio raíz: acá nunca se parte un host en el primer punto.
        var next = slug is null ? null : StoreDomain.CreateSubdomain(tenantId, slug, rootDomain, clock.UtcNow);

        // Si el hostname no cambió no se toca nada: borrar e insertar la misma clave en un guardado es un UPDATE inútil.
        if (next is not null && current.Count == 1 && string.Equals(current[0].Hostname, next.Hostname, StringComparison.Ordinal))
        {
            return;
        }

        dbContext.Domains.RemoveRange(current);

        if (next is not null)
        {
            dbContext.Domains.Add(next);
        }
    }
}
