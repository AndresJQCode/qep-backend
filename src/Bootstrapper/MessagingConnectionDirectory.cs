using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Bootstrapper;

/// <summary>Spec 2026-10-09 §6.4: el único punto donde Messaging e Integrations se tocan, y es acá a propósito. Spec 2026-10-14 §6.2:
/// también las conexiones de Instagram, que para Messaging son otro canal de la misma bandeja.</summary>
internal sealed class MessagingConnectionDirectory(
    IConnectionRoutes routes,
    IIntegrationConnections connections,
    IConnectionHealthReporter healthReporter) : IMessagingConnectionDirectory
{
    private static string Provider => IntegrationProviders.WhatsAppCloud.Key;

    private static string InstagramProvider => IntegrationProviders.Instagram.Key;

    public async Task<MessagingRoute?> FindRouteAsync(string phoneNumberId, CancellationToken cancellationToken) =>
        await routes.FindAsync(Provider, phoneNumberId, cancellationToken) is { } route ? ToRoute(route) : null;

    public async Task<MessagingRoute?> FindInstagramRouteAsync(string igUserId, CancellationToken cancellationToken) =>
        await routes.FindAsync(InstagramProvider, igUserId, cancellationToken) is { } route ? ToRoute(route) : null;

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

    public async Task<InstagramSender?> ResolveInstagramSenderAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var resolved = await connections.ResolveAsync(tenantId, connectionId, cancellationToken);
        if (resolved is null || !string.Equals(resolved.ProviderKey, InstagramProvider, StringComparison.Ordinal))
        {
            return null;
        }

        return resolved.Fields.TryGetValue(InstagramFieldKeys.IgUserId, out var igUserId)
            && resolved.Secrets.TryGetValue(InstagramFieldKeys.AccessToken, out var token)
                ? new InstagramSender(igUserId, token)
                : null;
    }

    public async Task<ConversationChannel?> FindChannelAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        if ((await connections.ListByProviderAsync(tenantId, InstagramProvider, cancellationToken)).Any(listing => listing.Id == connectionId))
        {
            return ConversationChannel.Instagram;
        }

        return (await connections.ListByProviderAsync(tenantId, Provider, cancellationToken)).Any(listing => listing.Id == connectionId)
            ? ConversationChannel.WhatsApp
            : null;
    }

    public async Task<MessagingTemplateSource?> ResolveTemplateSourceAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var resolved = await connections.ResolveAsync(tenantId, connectionId, cancellationToken);
        if (resolved is null || !string.Equals(resolved.ProviderKey, Provider, StringComparison.Ordinal))
        {
            return null;
        }

        return resolved.Fields.TryGetValue(WhatsAppCloudFieldKeys.WabaId, out var wabaId) && !string.IsNullOrWhiteSpace(wabaId)
            && resolved.Secrets.TryGetValue(WhatsAppCloudFieldKeys.AccessToken, out var token)
                ? new MessagingTemplateSource(wabaId, token)
                : null;
    }

    public async Task<IReadOnlyList<MessagingConnectionDto>> ListActiveAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        // El número (o el usuario de Instagram) que se muestra vive en los campos de la conexión, que sólo trae ResolveAsync (con el token
        // descifrado, que acá se descarta). Una conexión que no se resuelve (un secreto que no descifra) no sirve para enviar y no se ofrece.
        // WhatsApp primero y después Instagram, cada grupo por nombre: la pantalla de conversación nueva sólo ofrece las de WhatsApp.
        var result = new List<MessagingConnectionDto>();
        foreach (var summary in (await connections.ListActiveAsync(tenantId, Provider, cancellationToken)).OrderBy(connection => connection.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (await connections.ResolveAsync(tenantId, summary.Id, cancellationToken) is { } resolved)
            {
                result.Add(new MessagingConnectionDto(
                    summary.Id, summary.Name, resolved.Fields.GetValueOrDefault(WhatsAppCloudFieldKeys.DisplayPhoneNumber)));
            }
        }

        foreach (var summary in (await connections.ListActiveAsync(tenantId, InstagramProvider, cancellationToken)).OrderBy(connection => connection.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (await connections.ResolveAsync(tenantId, summary.Id, cancellationToken) is { } resolved)
            {
                result.Add(new MessagingConnectionDto(
                    summary.Id, summary.Name, null, nameof(ConversationChannel.Instagram), resolved.Fields.GetValueOrDefault(InstagramFieldKeys.Username)));
            }
        }

        return result;
    }

    public async Task<IReadOnlyDictionary<Guid, string>> ListNamesAsync(Guid tenantId, CancellationToken cancellationToken) =>
        (await connections.ListByProviderAsync(tenantId, Provider, cancellationToken))
            .Concat(await connections.ListByProviderAsync(tenantId, InstagramProvider, cancellationToken))
            .ToDictionary(listing => listing.Id, listing => listing.Name);

    public Task ReportRejectedAsync(Guid tenantId, Guid connectionId, string failureCode, CancellationToken cancellationToken) =>
        healthReporter.ReportCredentialsRejectedAsync(tenantId, connectionId, failureCode, cancellationToken);

    private static MessagingRoute ToRoute(ConnectionRoute route) => new(route.TenantId, route.ConnectionId, route.Status.ToString());
}
