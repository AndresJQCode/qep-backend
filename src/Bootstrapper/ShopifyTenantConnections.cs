using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Shopify.Application;

namespace Bootstrapper;

/// <summary>Spec 2026-10-15 §7 <c>GET /stores</c>: las conexiones de Shopify del tenant, en cualquier estado, con su nombre y el
/// dominio que guardó el enlace. Sin secretos: sólo campos.</summary>
internal sealed class ShopifyTenantConnections(IIntegrationConnectionRepository connections) : IShopifyTenantConnections
{
    public async Task<IReadOnlyList<ShopifyConnectionSummary>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
    [
        .. (await connections.ListAsync(tenantId, cancellationToken))
            .Where(connection => connection.TenantId == tenantId
                && string.Equals(connection.ProviderKey, IntegrationProviders.Shopify.Key, StringComparison.Ordinal))
            .Select(connection => new ShopifyConnectionSummary(
                connection.Id,
                connection.Name,
                connection.Fields.GetValueOrDefault(ShopifyFieldKeys.ShopDomain)?.Trim().ToLowerInvariant() is { Length: > 0 } domain
                    ? domain
                    : null,
                connection.Status.ToString())),
    ];
}
