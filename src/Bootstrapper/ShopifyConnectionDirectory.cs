using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Shopify.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-10-15 §5.2 y §5.3: de qué tenant y conexión es una tienda. El único punto donde Shopify e Integrations se tocan
/// por las conexiones, como <see cref="MessagingConnectionDirectory"/>. Cross-tenant a propósito (el webhook y el cron no tienen
/// tenant): cada lectura que sigue a la ruta va con el <c>tenantId</c> de la ruta.
/// <para>La ruta vuelve en cualquier estado: una conexión <c>Paused</c> o <c>NeedsAttention</c> sale con
/// <see cref="ShopifyConnectionRef.IsOperational"/> en <c>false</c>, para que un <c>app/uninstalled</c> o un pedido de una
/// tienda pausada actualicen el espejo en vez de perderse como <c>unknown_shop</c>. Los campos (el id de la tienda) se leen del
/// repositorio, que no mira el estado; el token, sólo de una conexión operativa.</para>
/// </summary>
internal sealed class ShopifyConnectionDirectory(
    IConnectionRoutes routes,
    IIntegrationConnectionRepository repository,
    IIntegrationConnections connections,
    ITenantModules tenantModules) : IShopifyConnectionDirectory
{
    private static string Provider => IntegrationProviders.Shopify.Key;

    public async Task<ShopifyConnectionRef?> FindByShopDomainAsync(string shopDomain, CancellationToken cancellationToken)
    {
        // La ruta guarda el dominio como lo normalizó el enlace (ShopifyShopDomain.TryNormalize): recortado y en minúsculas.
        var domain = shopDomain?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(domain))
        {
            return null;
        }

        return await routes.FindAsync(Provider, domain, cancellationToken) is { } route
            ? await ToRefAsync(route, domain, cancellationToken)
            : null;
    }

    public async Task<IReadOnlyList<ShopifyConnectionRef>> ListOperationalAsync(CancellationToken cancellationToken)
    {
        var result = new List<ShopifyConnectionRef>();
        foreach (var route in await routes.ListByProviderAsync(Provider, cancellationToken))
        {
            if (route.Status == ConnectionStatus.Active
                && await ToRefAsync(route, shopDomain: null, cancellationToken) is { IsOperational: true } connection)
            {
                result.Add(connection);
            }
        }

        return result;
    }

    private async Task<ShopifyConnectionRef?> ToRefAsync(ConnectionRoute route, string? shopDomain, CancellationToken cancellationToken)
    {
        var connection = await repository.FindAsync(route.TenantId, route.ConnectionId, cancellationToken);
        if (connection is null)
        {
            // La ruta se borra en cascada con la conexión; una sin conexión es la carrera con ese borrado.
            return null;
        }

        var domain = shopDomain
            ?? connection.Fields.GetValueOrDefault(ShopifyFieldKeys.ShopDomain)?.Trim().ToLowerInvariant()
            ?? string.Empty;
        var shopId = connection.Fields.GetValueOrDefault(ShopifyFieldKeys.ShopId) ?? string.Empty;
        var token = await OperationalTokenAsync(route, cancellationToken);
        return new ShopifyConnectionRef(route.TenantId, route.ConnectionId, shopId, domain, token is not null, token);
    }

    // Operativa = conexión Active, módulo shopify efectivo en el tenant (§5.2) y un token que descifra. El módulo se revisa acá
    // aunque ResolveAsync ya mire la visibilidad del proveedor: es la regla de Shopify, no un detalle de Integrations.
    private async Task<string?> OperationalTokenAsync(ConnectionRoute route, CancellationToken cancellationToken)
    {
        if (route.Status != ConnectionStatus.Active
            || await tenantModules.FindAsync(route.TenantId, cancellationToken) is not { } modules
            || !modules.IsEnabled(TenantModuleKeys.Shopify))
        {
            return null;
        }

        var resolved = await connections.ResolveAsync(route.TenantId, route.ConnectionId, cancellationToken);
        return resolved is not null
            && string.Equals(resolved.ProviderKey, Provider, StringComparison.Ordinal)
            && resolved.Secrets.TryGetValue(ShopifyFieldKeys.AccessToken, out var token)
            && !string.IsNullOrWhiteSpace(token)
                ? token
                : null;
    }
}
