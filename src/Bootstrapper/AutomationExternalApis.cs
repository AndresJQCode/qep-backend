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

    /// <summary>Integrations ya filtra tenant, estado <c>Active</c>, proveedor habilitado y clave legible, y dice cuál falló (spec 2026-10-24
    /// D-T4); acá se descarta además otro proveedor (un bloque importado con el id de una conexión de WhatsApp) y una URL que ni siquiera se
    /// arma o una clave vacía. Las credenciales nunca viajan con un motivo.</summary>
    public async Task<ExternalApiResolution> ResolveAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var resolution = await connections.ResolveAsync(tenantId, connectionId, cancellationToken);
        if (resolution.Connection is not { } connection)
        {
            return new ExternalApiResolution(
                null, ConnectionReasonCodes.Of(resolution.Unavailable ?? ConnectionUnavailableReason.NotFound), resolution.Name);
        }

        if (!string.Equals(connection.ProviderKey, ProviderKey, StringComparison.Ordinal))
        {
            return new ExternalApiResolution(null, ConnectionReasonCodes.WrongProvider, connection.Name);
        }

        if (!Uri.TryCreate(connection.Fields.GetValueOrDefault(ExternalApiFieldKeys.BaseUrl), UriKind.Absolute, out var baseUrl)
            || connection.Secrets.GetValueOrDefault(ExternalApiFieldKeys.ApiKey) is not { Length: > 0 } apiKey)
        {
            return new ExternalApiResolution(null, ConnectionReasonCodes.Incomplete, connection.Name);
        }

        var authHeader = connection.Fields.GetValueOrDefault(ExternalApiFieldKeys.AuthHeader) is { } header && !string.IsNullOrWhiteSpace(header)
            ? header
            : DefaultAuthHeader;
        return new ExternalApiResolution(new ExternalApiCredentials(baseUrl, authHeader, apiKey), null, connection.Name);
    }
}
