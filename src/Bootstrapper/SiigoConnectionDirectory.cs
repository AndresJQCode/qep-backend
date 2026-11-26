using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Siigo.Application;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-11-18 §5.1 y §5.3: las conexiones de Siigo de Integrations, por el puerto de Siigo. Calcado de
/// <see cref="AiProviderCredentials"/>: <c>IIntegrationConnections</c> ya filtra tenant, estado, visibilidad y clave legible; acá se
/// traduce a la causa de Siigo (<see cref="ConnectionReasonCodes"/>) y se arma <see cref="SiigoCredentials"/>. Una conexión de otro
/// proveedor es <c>not_found</c>: no se distingue de una inexistente, como la de otro tenant. Nunca imprime la clave de acceso.
/// </summary>
internal sealed class SiigoConnectionDirectory(IIntegrationConnectionRepository repository, IIntegrationConnections connections)
    : ISiigoConnectionDirectory
{
    private static string Provider => IntegrationProviders.Siigo.Key;

    public async Task<SiigoConnectionInfo?> FindAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        // El repositorio filtra por tenant: la conexión de otro tenant llega como null.
        var connection = await repository.FindAsync(tenantId, connectionId, cancellationToken);
        return connection is not null
            && connection.TenantId == tenantId
            && string.Equals(connection.ProviderKey, Provider, StringComparison.Ordinal)
                ? new SiigoConnectionInfo(connection.Id, connection.Name, connection.Status.ToString())
                : null;
    }

    public async Task<SiigoConnectionResolution> ResolveAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        ToSiigo(await connections.ResolveAsync(tenantId, connectionId, cancellationToken));

    public async Task<SiigoConnectionResolution> ResolveForSetupAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        ToSiigo(await connections.ResolveForSetupAsync(tenantId, connectionId, cancellationToken));

    private static SiigoConnectionResolution ToSiigo(ConnectionResolution resolution)
    {
        if (resolution.Connection is not { } connection)
        {
            return new SiigoConnectionResolution(null, ConnectionReasonCodes.Of(resolution.Unavailable!.Value));
        }

        if (!string.Equals(connection.ProviderKey, Provider, StringComparison.Ordinal))
        {
            return new SiigoConnectionResolution(null, ConnectionReasonCodes.Of(ConnectionUnavailableReason.NotFound));
        }

        return connection.Fields.GetValueOrDefault(SiigoFieldKeys.Username) is { Length: > 0 } username
            && connection.Secrets.GetValueOrDefault(SiigoFieldKeys.AccessKey) is { Length: > 0 } accessKey
                ? new SiigoConnectionResolution(new SiigoCredentials(connection.Id, username, accessKey), null)
                : new SiigoConnectionResolution(null, ConnectionReasonCodes.Incomplete);
    }

    public async Task<IReadOnlyList<SiigoConnectionInfo>> ListByTenantAsync(Guid tenantId, CancellationToken cancellationToken) =>
        [
            .. (await connections.ListByProviderAsync(tenantId, Provider, cancellationToken))
                .Select(listing => new SiigoConnectionInfo(listing.Id, listing.Name, listing.Status.ToString())),
        ];
}
