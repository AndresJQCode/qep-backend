using Modules.Ai.Application;
using Modules.Ai.Domain;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;

namespace Bootstrapper;

/// <summary>
/// Plan 2026-11-12, C-1: la clave de cada proveedor de modelos es la única conexión de ese proveedor en Integrations
/// (<c>maxConnections: 1</c>), con la misma clave de proveedor que Ai (<c>anthropic</c>). Integrations ya filtra tenant, estado,
/// visibilidad y clave legible; acá se traduce a la causa de Ai. Un proveedor de Ai que Integrations todavía no ofrece (OpenAI hasta la
/// Tarea 6) no tiene conexión posible: <c>NotConfigured</c> y <c>connectable: false</c>.
/// </summary>
internal sealed class AiProviderCredentials(IIntegrationConnections connections) : IAiProviderCredentials
{
    public async Task<AiCredentialResolution> ResolveAsync(Guid tenantId, AiProvider provider, CancellationToken cancellationToken)
    {
        if (IntegrationProviders.Find(provider.Value) is null)
        {
            return AiCredentialResolution.Unusable(AiCredentialUnavailableReason.NotConfigured);
        }

        var listing = await FirstAsync(tenantId, provider, cancellationToken);
        if (listing is null)
        {
            return AiCredentialResolution.Unusable(AiCredentialUnavailableReason.NotConfigured);
        }

        var resolution = await connections.ResolveAsync(tenantId, listing.Id, cancellationToken);
        if (resolution.Connection is not { } connection)
        {
            return AiCredentialResolution.Unusable(ReasonOf(resolution.Unavailable));
        }

        return connection.Secrets.GetValueOrDefault(AnthropicFieldKeys.ApiKey) is { Length: > 0 } apiKey
            ? AiCredentialResolution.Resolved(new AiCredential(connection.Id, apiKey))
            : AiCredentialResolution.Unusable(AiCredentialUnavailableReason.SecretUnreadable);
    }

    public async Task<IReadOnlyList<AiProviderConnectionState>> DescribeAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var states = new List<AiProviderConnectionState>();
        foreach (var provider in AiProviders.All)
        {
            if (IntegrationProviders.Find(provider.Value) is null)
            {
                states.Add(new AiProviderConnectionState(provider, Connectable: false, null, null));
                continue;
            }

            var listing = await FirstAsync(tenantId, provider, cancellationToken);
            states.Add(new AiProviderConnectionState(provider, Connectable: true, listing?.Id, listing?.Status.ToString()));
        }

        return states;
    }

    // maxConnections: 1, así que hay una o ninguna; si algún día hubiera más, gana la primera por nombre (el orden de Integrations).
    private async Task<ConnectionListing?> FirstAsync(Guid tenantId, AiProvider provider, CancellationToken cancellationToken)
    {
        var listings = await connections.ListByProviderAsync(tenantId, provider.Value, cancellationToken);
        return listings.Count > 0 ? listings[0] : null;
    }

    // Oculto o deshabilitado (el tenant perdió el módulo o el proveedor) es, para el agente, «no hay clave».
    private static AiCredentialUnavailableReason ReasonOf(ConnectionUnavailableReason? reason) => reason switch
    {
        ConnectionUnavailableReason.Paused => AiCredentialUnavailableReason.Paused,
        ConnectionUnavailableReason.NeedsAttention => AiCredentialUnavailableReason.NeedsAttention,
        ConnectionUnavailableReason.SecretUnreadable => AiCredentialUnavailableReason.SecretUnreadable,
        _ => AiCredentialUnavailableReason.NotConfigured,
    };
}
