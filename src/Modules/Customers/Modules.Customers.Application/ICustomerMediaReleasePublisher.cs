namespace Modules.Customers.Application;

/// <summary>
/// Encola el aviso de que los adjuntos de un cliente borrado quedaron sin dueño, para que Messaging purgue sus objetos de R2
/// (<see cref="CustomersIntegrationEvents.CustomerMediaReleased"/>). Va por outbox: commitea en la misma transacción que el
/// borrado, o no sale nada, y un fallo de R2 no puede dejar el borrado a medias (spec 2026-11-10 A-9).
///
/// No hace nada con una colección vacía y deduplica las claves.
/// </summary>
public interface ICustomerMediaReleasePublisher
{
    void Publish(Guid tenantId, Guid customerId, IReadOnlyCollection<string> storageKeys, DateTimeOffset occurredAt);
}
