using Modules.Ai.Application;
using Modules.Ai.Domain;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;

namespace Bootstrapper;

/// <summary>
/// Plan 2026-11-12, C-1: la clave de cada proveedor de modelos es la única conexión de ese proveedor en Integrations
/// (<c>maxConnections: 1</c>), con la misma clave de proveedor que Ai (<c>anthropic</c>, <c>openai</c>). Integrations ya filtra tenant,
/// estado, visibilidad y clave legible; acá se traduce a la causa de Ai. Un proveedor de Ai que Integrations no ofrezca no tiene conexión
/// posible: <c>NotConfigured</c> y <c>connectable: false</c>.
/// </summary>
internal sealed class AiProviderCredentials(IIntegrationConnections connections, IConnectionHealthReporter healthReporter)
    : IAiProviderCredentials
{
    /// <summary>El campo secreto de cada proveedor de Integrations que es un proveedor de modelos.</summary>
    private static readonly Dictionary<string, string> ApiKeyFields = new(StringComparer.Ordinal)
    {
        [IntegrationProviders.Anthropic.Key] = AnthropicFieldKeys.ApiKey,
        [IntegrationProviders.OpenAi.Key] = OpenAiFieldKeys.ApiKey,
    };

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

        return ApiKeyFields.TryGetValue(provider.Value, out var field)
            && connection.Secrets.GetValueOrDefault(field) is { Length: > 0 } apiKey
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
            // Plan 2026-11-17 A1: MarkVerified saca de NeedsAttention sin limpiar last_failure_code; fuera de ese estado sería un fallo viejo.
            var failureCode = listing?.Status == ConnectionStatus.NeedsAttention ? listing.LastFailureCode : null;
            states.Add(new AiProviderConnectionState(provider, Connectable: true, listing?.Id, listing?.Status.ToString(), failureCode));
        }

        return states;
    }

    /// <summary>Spec 2026-10-29 §4.5 y C-19: por el reportero de Integrations, como Mensajería con Meta: de <c>Active</c> a <c>NeedsAttention</c>,
    /// auditado y publicado en la misma transacción. Una prueba exitosa de la clave la devuelve a <c>Active</c>.</summary>
    public Task MarkNeedsAttentionAsync(Guid tenantId, Guid connectionId, string failureCode, CancellationToken cancellationToken) =>
        healthReporter.ReportCredentialsRejectedAsync(tenantId, connectionId, failureCode, cancellationToken);

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
