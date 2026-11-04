using Modules.Store.Domain;

namespace Modules.Store.Application;

public interface IStoreDomainRepository
{
    /// <summary>Sin rastrear: la resolución pública sólo lee.</summary>
    Task<StoreDomain?> FindByHostAsync(string hostname, CancellationToken cancellationToken);

    /// <summary>
    /// Deja a lo sumo una fila <see cref="StoreDomainKind.Subdomain"/> para el tenant: borra la vigente
    /// y, si <paramref name="hostname"/> no es null, inserta la nueva en la misma unidad de trabajo.
    /// Un hostname null sólo borra (la tienda quedó sin slug).
    /// </summary>
    Task ReplaceSubdomainAsync(Guid tenantId, string? hostname, CancellationToken cancellationToken);
}
