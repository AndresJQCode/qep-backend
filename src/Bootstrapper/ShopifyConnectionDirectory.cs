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

    // «Sincronizar ahora»: la conexión del tenant por su id y, con su dominio, la ruta. La ruta tiene que apuntar a esta misma
    // conexión del mismo tenant: si la tienda se reenlazó a otro lado, esta conexión ya no recibe nada y no se sincroniza.
    public async Task<ShopifyConnectionRef?> FindAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var connection = await repository.FindAsync(tenantId, connectionId, cancellationToken);
        if (connection is null
            || connection.TenantId != tenantId
            || !string.Equals(connection.ProviderKey, Provider, StringComparison.Ordinal)
            || connection.Fields.GetValueOrDefault(ShopifyFieldKeys.ShopDomain)?.Trim().ToLowerInvariant() is not { Length: > 0 } domain)
        {
            return null;
        }

        return await routes.FindAsync(Provider, domain, cancellationToken) is { } route
            && route.TenantId == tenantId
            && route.ConnectionId == connectionId
                ? await ToRefAsync(route, domain, cancellationToken)
                : null;
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
        // D-S4: el enlace crea la conexión (LinkShopifyStore), así que su CreatedAt es el instante del enlace.
        return new ShopifyConnectionRef(route.TenantId, route.ConnectionId, shopId, domain, token is not null, token)
        {
            LinkedAt = connection.CreatedAt,
        };
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

        var resolved = (await connections.ResolveAsync(route.TenantId, route.ConnectionId, cancellationToken)).Connection;
        return resolved is not null
            && string.Equals(resolved.ProviderKey, Provider, StringComparison.Ordinal)
            && resolved.Secrets.TryGetValue(ShopifyFieldKeys.AccessToken, out var token)
            && !string.IsNullOrWhiteSpace(token)
                ? token
                : null;
    }
}
