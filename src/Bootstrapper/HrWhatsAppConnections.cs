using Modules.Hr.Application.BirthdayNotices;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-11-26 §5.1: whether an id is a usable WhatsApp connection of the tenant, for the birthday notices setting. Integrations
/// already filters state, visibility and provider enablement in <c>ListResolvedActiveAsync</c> (as
/// <see cref="WholesaleWhatsAppConnections"/> relies on); a connection of another tenant, inactive or of another provider is simply
/// not in that list. HR knows nothing else about WhatsApp.
/// </summary>
internal sealed class HrWhatsAppConnections(IIntegrationConnections connections) : IHrWhatsAppConnections
{
    public async Task<bool> IsWhatsAppConnectionAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        (await connections.ListResolvedActiveAsync(tenantId, IntegrationProviders.WhatsAppCloud.Key, cancellationToken))
            .Any(connection => connection.Id == connectionId);
}
