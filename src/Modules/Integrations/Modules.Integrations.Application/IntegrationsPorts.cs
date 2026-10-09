using Modules.Integrations.Domain;

namespace Modules.Integrations.Application;

/// <summary>
/// El catálogo como puerto (P5 del plan): el código usa <see cref="IntegrationProviderCatalog"/>, que es
/// <see cref="IntegrationProviders.All"/>; las pruebas inyectan uno con un proveedor falso para
/// demostrar el criterio 5 del spec sin tocar los handlers.
/// </summary>
public interface IIntegrationProviderCatalog
{
    IReadOnlyList<IntegrationProvider> All { get; }

    IntegrationProvider? Find(string? key);
}

public sealed class IntegrationProviderCatalog : IIntegrationProviderCatalog
{
    public IReadOnlyList<IntegrationProvider> All => IntegrationProviders.All;

    public IntegrationProvider? Find(string? key) => IntegrationProviders.Find(key);
}

/// <summary>Todo método recibe <c>tenantId</c>: el id de otro tenant responde igual que uno inexistente.</summary>
public interface IIntegrationConnectionRepository
{
    /// <summary>Con tracking, secretos incluidos.</summary>
    Task<IntegrationConnection?> FindAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    /// <summary>Todas las del tenant, sin tracking y sin orden: el handler ordena. Hay tope de 20 por
    /// proveedor, así que no se pagina.</summary>
    Task<IReadOnlyList<IntegrationConnection>> ListAsync(Guid tenantId, CancellationToken cancellationToken);

    Task<int> CountAsync(Guid tenantId, string providerKey, CancellationToken cancellationToken);

    void Add(IntegrationConnection connection);

    void Remove(IntegrationConnection connection);
}

/// <summary>
/// El nombre de quien creó cada conexión (<c>createdBy.displayName</c>). Lo resuelve un adaptador en
/// Bootstrapper —membresía de Tenancy y correo de Identity—, como <c>PosCashierLookup</c>: Integrations
/// no referencia Identity (P23).
/// </summary>
public interface IConnectionAuthorNames
{
    /// <summary>Sólo las membresías del tenant; una que no está, no aparece en el diccionario.</summary>
    Task<IReadOnlyDictionary<Guid, string>> FindAsync(
        Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken);
}

/// <summary>Spec 2026-10-09 §6.1: la ruta se crea en la misma transacción que la conexión.</summary>
public interface IConnectionRouteRepository
{
    void Add(IntegrationConnectionRoute route);

    /// <summary>Lectura indexada previa al canje (§8.1, paso 3); la garantía final la da el índice único.</summary>
    Task<bool> ExistsAsync(string providerKey, string externalId, CancellationToken cancellationToken);
}

public sealed record ConnectionRoute(Guid TenantId, Guid ConnectionId, ConnectionStatus Status);

/// <summary>
/// Cross-tenant a propósito: el webhook no sabe de qué tenant es el número hasta resolver la ruta. Lo
/// usa sólo el adaptador de Messaging en Bootstrapper. <c>null</c> = número desconocido o conexión borrada.
/// </summary>
public interface IConnectionRoutes
{
    Task<ConnectionRoute?> FindAsync(string providerKey, string externalId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ConnectionRoute>> FindByAccountAsync(string providerKey, string accountId, CancellationToken cancellationToken);
}
