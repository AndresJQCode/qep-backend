using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Shopify.Application;

namespace Bootstrapper;

/// <summary>Spec 2026-10-15 §5.2 y §5.3: Shopify le avisa a Integrations que la tienda dejó de responder. Los dos caminos pasan la
/// conexión a <c>NeedsAttention</c> con su código; reportar dos veces no duplica nada (<see cref="IConnectionHealthReporter"/>).</summary>
internal sealed class ShopifyConnectionHealth(IConnectionHealthReporter reporter) : IShopifyConnectionHealth
{
    public Task ReportUninstalledAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        reporter.ReportCredentialsRejectedAsync(tenantId, connectionId, ConnectionFailureCodes.AppUninstalled, cancellationToken);

    public Task ReportRejectedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        reporter.ReportCredentialsRejectedAsync(tenantId, connectionId, ConnectionFailureCodes.CredentialsRejected, cancellationToken);
}
