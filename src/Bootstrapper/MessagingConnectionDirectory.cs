using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Messaging.Application;

namespace Bootstrapper;

/// <summary>Spec 2026-10-09 §6.4: el único punto donde Messaging e Integrations se tocan, y es acá a propósito.</summary>
internal sealed class MessagingConnectionDirectory(
    IConnectionRoutes routes,
    IIntegrationConnections connections,
    IConnectionHealthReporter healthReporter) : IMessagingConnectionDirectory
{
    private static string Provider => IntegrationProviders.WhatsAppCloud.Key;

    public async Task<MessagingRoute?> FindRouteAsync(string phoneNumberId, CancellationToken cancellationToken) =>
        await routes.FindAsync(Provider, phoneNumberId, cancellationToken) is { } route ? ToRoute(route) : null;

    public async Task<IReadOnlyList<MessagingRoute>> FindByAccountAsync(string wabaId, CancellationToken cancellationToken) =>
        (await routes.FindByAccountAsync(Provider, wabaId, cancellationToken)).Select(ToRoute).ToArray();

    public async Task<MessagingSender?> ResolveSenderAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var resolved = await connections.ResolveAsync(tenantId, connectionId, cancellationToken);
        if (resolved is null || !string.Equals(resolved.ProviderKey, Provider, StringComparison.Ordinal))
        {
            return null;
        }

        return resolved.Fields.TryGetValue(WhatsAppCloudFieldKeys.PhoneNumberId, out var phoneNumberId)
            && resolved.Secrets.TryGetValue(WhatsAppCloudFieldKeys.AccessToken, out var token)
                ? new MessagingSender(phoneNumberId, token)
                : null;
    }

    public async Task<IReadOnlyDictionary<Guid, string>> ListNamesAsync(Guid tenantId, CancellationToken cancellationToken) =>
        (await connections.ListByProviderAsync(tenantId, Provider, cancellationToken))
            .ToDictionary(listing => listing.Id, listing => listing.Name);

    public Task ReportRejectedAsync(Guid tenantId, Guid connectionId, string failureCode, CancellationToken cancellationToken) =>
        healthReporter.ReportCredentialsRejectedAsync(tenantId, connectionId, failureCode, cancellationToken);

    private static MessagingRoute ToRoute(ConnectionRoute route) => new(route.TenantId, route.ConnectionId, route.Status.ToString());
}
