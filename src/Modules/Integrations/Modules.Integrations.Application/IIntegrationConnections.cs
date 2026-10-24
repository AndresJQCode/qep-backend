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

/// <summary>Spec 2026-10-24 D-T4: por qué una conexión no se puede usar; el primero que aplica, en este orden.</summary>
public enum ConnectionUnavailableReason
{
    /// <summary>No existe o es de otro tenant (el repositorio filtra por tenant).</summary>
    NotFound,

    /// <summary>Está Paused.</summary>
    Paused,

    /// <summary>Está NeedsAttention.</summary>
    NeedsAttention,

    /// <summary>El catálogo no conoce el proveedor, o no es visible con los módulos del tenant.</summary>
    ProviderHidden,

    /// <summary>Visible pero no habilitado para el tenant.</summary>
    ProviderDisabled,

    /// <summary>Algún secreto no descifra.</summary>
    SecretUnreadable,
}

/// <summary>
/// Spec 2026-10-24 D-T4: el resultado de <see cref="IIntegrationConnections.ResolveAsync"/>. <see cref="Connection"/> no es
/// <c>null</c> sólo si <see cref="Unavailable"/> es <c>null</c>; el tipo obliga a cada consumidor a decidir qué hace con la
/// causa en vez de recibir un <c>null</c> mudo.
/// </summary>
/// <param name="Name">El nombre de la conexión cuando existe, también si no se puede usar; <c>null</c> en NotFound.</param>
public sealed record ConnectionResolution(ResolvedConnection? Connection, ConnectionUnavailableReason? Unavailable, string? Name)
{
    public static ConnectionResolution Resolved(ResolvedConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return new ConnectionResolution(connection, null, connection.Name);
    }

    public static ConnectionResolution Unusable(ConnectionUnavailableReason reason, string? name) => new(null, reason, name);
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
    /// <summary>Sólo Active, visible y con el proveedor habilitado para el tenant (plan 2026-10-21). Si no, la resolución
    /// trae <c>Connection = null</c> y el motivo (spec 2026-10-24 D-T4): no existe o es de otro tenant, está Paused o
    /// NeedsAttention, su proveedor está oculto o deshabilitado, o un secreto no descifra. El consumidor decide qué hacer
    /// con "no hay conexión".</summary>
    Task<ConnectionResolution> ResolveAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    /// <summary>Las Active de un proveedor, para que la pantalla del consumidor deje elegir una; vacío si el proveedor no
    /// es visible o no está habilitado para el tenant.</summary>
    Task<IReadOnlyList<ConnectionSummary>> ListActiveAsync(Guid tenantId, string providerKey, CancellationToken cancellationToken);

    /// <summary>Todas las del proveedor en el tenant, por nombre; no filtra por estado, visibilidad ni habilitación.</summary>
    Task<IReadOnlyList<ConnectionListing>> ListByProviderAsync(Guid tenantId, string providerKey, CancellationToken cancellationToken);
}

public sealed class IntegrationConnections(
    IIntegrationConnectionRepository repository,
    IIntegrationProviderCatalog catalog,
    ITenantModules tenantModules,
    ITenantProviders tenantProviders,
    ISecretProtector protector) : IIntegrationConnections
{
    public async Task<ConnectionResolution> ResolveAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var connection = await repository.FindAsync(tenantId, connectionId, cancellationToken);
        if (connection is null)
        {
            return ConnectionResolution.Unusable(ConnectionUnavailableReason.NotFound, null);
        }

        if (connection.Status != ConnectionStatus.Active)
        {
            return ConnectionResolution.Unusable(
                connection.Status == ConnectionStatus.Paused
                    ? ConnectionUnavailableReason.Paused
                    : ConnectionUnavailableReason.NeedsAttention,
                connection.Name);
        }

        if (catalog.Find(connection.ProviderKey) is not { } provider
            || !provider.IsVisibleFor(await tenantModules.FindAsync(tenantId, cancellationToken)))
        {
            return ConnectionResolution.Unusable(ConnectionUnavailableReason.ProviderHidden, connection.Name);
        }

        if (!await ProviderVisibility.IsEnabledAsync(tenantProviders, tenantId, provider, cancellationToken))
        {
            return ConnectionResolution.Unusable(ConnectionUnavailableReason.ProviderDisabled, connection.Name);
        }

        var stored = ConnectionSecrets.Read(protector, connection);
        if (stored.Unreadable.Count > 0)
        {
            return ConnectionResolution.Unusable(ConnectionUnavailableReason.SecretUnreadable, connection.Name);
        }

        return ConnectionResolution.Resolved(new ResolvedConnection(
            connection.Id,
            connection.ProviderKey,
            connection.Name,
            new Dictionary<string, string>(connection.Fields, StringComparer.Ordinal),
            stored.Plain));
    }

    public async Task<IReadOnlyList<ConnectionSummary>> ListActiveAsync(
        Guid tenantId, string providerKey, CancellationToken cancellationToken)
    {
        if (catalog.Find(providerKey) is not { } provider
            || !provider.IsVisibleFor(await tenantModules.FindAsync(tenantId, cancellationToken))
            || !await ProviderVisibility.IsEnabledAsync(tenantProviders, tenantId, provider, cancellationToken))
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
