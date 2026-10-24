using Modules.Automations.Application;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;

namespace Bootstrapper;

/// <summary>Spec 2026-10-21 D-H4: las conexiones «API externa» del tenant, sobre <see cref="IIntegrationConnections"/>. La lista trae todas, en
/// cualquier estado: el validador distingue activas de pausadas. <see cref="ResolveAsync"/> (D-H5) trae la clave sólo de una activa.</summary>
internal sealed class AutomationExternalApis(IIntegrationConnections connections) : IAutomationExternalApis
{
    public const string ProviderKey = "external-api";

    /// <summary>Sin header guardado, el mismo default que el probador de la conexión (<c>ExternalApiConnectionTester</c>).</summary>
    private const string DefaultAuthHeader = "x-api-key";

    public async Task<IReadOnlyList<ExternalApiRef>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
        [.. (await connections.ListByProviderAsync(tenantId, ProviderKey, cancellationToken))
            .Select(connection => new ExternalApiRef(connection.Id, connection.Name, connection.Status.ToString()))];

    /// <summary>Integrations ya filtra tenant, estado <c>Active</c>, proveedor habilitado y clave legible; acá se descarta otro proveedor (un
    /// bloque importado con el id de una conexión de WhatsApp) y una URL que ni siquiera se arma.</summary>
    public async Task<ExternalApiCredentials?> ResolveAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        if ((await connections.ResolveAsync(tenantId, connectionId, cancellationToken)).Connection is not { ProviderKey: ProviderKey } connection
            || !Uri.TryCreate(connection.Fields.GetValueOrDefault(ExternalApiFieldKeys.BaseUrl), UriKind.Absolute, out var baseUrl)
            || connection.Secrets.GetValueOrDefault(ExternalApiFieldKeys.ApiKey) is not { Length: > 0 } apiKey)
        {
            return null;
        }

        var authHeader = connection.Fields.GetValueOrDefault(ExternalApiFieldKeys.AuthHeader) is { } header && !string.IsNullOrWhiteSpace(header)
            ? header
            : DefaultAuthHeader;
        return new ExternalApiCredentials(baseUrl, authHeader, apiKey);
    }
}
