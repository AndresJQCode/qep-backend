using Modules.Store.Domain;

namespace Modules.Store.Application;

public interface IStoreDomainRepository
{
    /// <summary>Sin rastrear: la resolución pública sólo lee.</summary>
    Task<StoreDomain?> FindByHostAsync(string hostname, CancellationToken cancellationToken);

    /// <summary>
    /// Deja a lo sumo una fila <see cref="StoreDomainKind.Subdomain"/> para el tenant: borra la vigente y, si <paramref name="slug"/> no
    /// es null, inserta <c>{slug}.{rootDomain}</c>. Todo en el contexto del módulo, sin guardar: el manejador la guarda en la misma
    /// unidad de trabajo que la tienda, porque ninguna restricción de la base garantiza una sola fila Subdomain por tenant. Un slug
    /// null sólo borra (la tienda quedó sin slug).
    /// </summary>
    Task ReplaceSubdomainAsync(Guid tenantId, string? slug, string rootDomain, CancellationToken cancellationToken);
}
