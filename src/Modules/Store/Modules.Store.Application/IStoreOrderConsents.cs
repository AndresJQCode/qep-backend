using Modules.Store.Domain;

namespace Modules.Store.Application;

/// <summary>
/// Consentimientos de los pedidos directos de la tienda. Se guarda con <see cref="IStoreUnitOfWork"/>, en el mismo guardado que la
/// auditoría.
/// </summary>
public interface IStoreOrderConsents
{
    void Add(StoreOrderConsent consent);

    // Sólo lo usan las pruebas.
    Task<StoreOrderConsent?> FindAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken);
}
