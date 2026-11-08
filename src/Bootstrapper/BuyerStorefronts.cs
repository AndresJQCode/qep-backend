using Modules.Buyers.Application;
using Modules.Store.Application;

namespace Bootstrapper;

/// <summary>
/// Store → Buyers: el host de la ruta resuelto con las mismas reglas que la tienda pública. Una tienda sin publicar, un host
/// ajeno, un tenant inactivo o con el módulo apagado lanzan el mismo 404 <c>store.public.not_found</c>, sin cambios. El nombre que ve
/// el comprador es el de la tienda y, si no tiene, el del tenant (como <c>GET /</c> de la tienda pública).
/// </summary>
internal sealed class BuyerStorefronts(StorefrontResolver resolver) : IBuyerStorefronts
{
    public async Task<BuyerStorefront> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        var (store, tenant) = await resolver.ResolveAsync(host, cancellationToken);
        return new BuyerStorefront(host.Trim().ToLowerInvariant(), store.Name ?? tenant.DisplayName);
    }
}
