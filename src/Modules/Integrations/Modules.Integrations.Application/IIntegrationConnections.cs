using Modules.Integrations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Integrations.Application;

/// <summary>
/// Una conexión lista para usar. Los secretos vienen en claro, en memoria, para ese request: el
/// consumidor no los persiste ni los registra. <see cref="ToString"/> no los imprime.
/// </summary>
public sealed record ResolvedConnection(
    Guid Id,
    string ProviderKey,
    string Name,
    IReadOnlyDictionary<string, string> Fields,
    IReadOnlyDictionary<string, string> Secrets)
{
    public override string ToString() =>
        $"ResolvedConnection {{ Id = {Id}, ProviderKey = {ProviderKey}, Name = {Name} }}";
}

public sealed record ConnectionSummary(Guid Id, string ProviderKey, string Name);

/// <summary>Nombre y estado de una conexión del proveedor, Active o no (spec 2026-10-09 §6.1): la
/// bandeja muestra <c>connectionName</c> también en conversaciones de una conexión pausada.</summary>
public sealed record ConnectionListing(Guid Id, string Name, ConnectionStatus Status);

/// <summary>
/// Spec 2026-10-08, «Puertos para los consumidores». Cada consumidor declara su propio puerto en su
/// Application y el adaptador vive en Bootstrapper: un módulo de negocio nunca referencia
/// <c>Modules.Integrations.Application</c> (IntegrationsLayerTests).
/// </summary>
public interface IIntegrationConnections
{
    /// <summary>Sólo Active y visible para el tenant. <c>null</c> si no existe, es de otro tenant, está
    /// Paused o NeedsAttention, o un secreto no descifra (DECISIÓN-PENDIENTE 1): el consumidor decide qué
    /// hacer con "no hay conexión".</summary>
    Task<ResolvedConnection?> ResolveAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    /// <summary>Las Active de un proveedor, para que la pantalla del consumidor deje elegir una.</summary>
    Task<IReadOnlyList<ConnectionSummary>> ListActiveAsync(Guid tenantId, string providerKey, CancellationToken cancellationToken);

    /// <summary>Todas las del proveedor en el tenant, por nombre; no filtra por estado ni por visibilidad.</summary>
    Task<IReadOnlyList<ConnectionListing>> ListByProviderAsync(Guid tenantId, string providerKey, CancellationToken cancellationToken);
}

public sealed class IntegrationConnections(
    IIntegrationConnectionRepository repository,
    IIntegrationProviderCatalog catalog,
    ITenantModules tenantModules,
    ISecretProtector protector) : IIntegrationConnections
{
    public async Task<ResolvedConnection?> ResolveAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var connection = await repository.FindAsync(tenantId, connectionId, cancellationToken);
        if (connection is not { Status: ConnectionStatus.Active }
            || catalog.Find(connection.ProviderKey) is not { } provider
            || !provider.IsVisibleFor(await tenantModules.FindAsync(tenantId, cancellationToken)))
        {
            return null;
        }

        var stored = ConnectionSecrets.Read(protector, connection);
        if (stored.Unreadable.Count > 0)
        {
            return null;
        }

        return new ResolvedConnection(
            connection.Id,
            connection.ProviderKey,
            connection.Name,
            new Dictionary<string, string>(connection.Fields, StringComparer.Ordinal),
            stored.Plain);
    }

    public async Task<IReadOnlyList<ConnectionSummary>> ListActiveAsync(
        Guid tenantId, string providerKey, CancellationToken cancellationToken)
    {
        if (catalog.Find(providerKey) is not { } provider
            || !provider.IsVisibleFor(await tenantModules.FindAsync(tenantId, cancellationToken)))
        {
            return [];
        }

        return (await repository.ListAsync(tenantId, cancellationToken))
            .Where(connection => connection.Status == ConnectionStatus.Active
                && string.Equals(connection.ProviderKey, provider.Key, StringComparison.Ordinal))
            .OrderBy(connection => connection.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(connection => connection.Id)
            .Select(connection => new ConnectionSummary(connection.Id, connection.ProviderKey, connection.Name))
            .ToArray();
    }

    public async Task<IReadOnlyList<ConnectionListing>> ListByProviderAsync(
        Guid tenantId, string providerKey, CancellationToken cancellationToken) =>
        (await repository.ListAsync(tenantId, cancellationToken))
            .Where(connection => string.Equals(connection.ProviderKey, providerKey, StringComparison.Ordinal))
            .OrderBy(connection => connection.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(connection => connection.Id)
            .Select(connection => new ConnectionListing(connection.Id, connection.Name, connection.Status))
            .ToArray();
}
