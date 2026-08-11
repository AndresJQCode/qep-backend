using Modules.Tenancy.Infrastructure.Persistence;

namespace Modules.Tenancy.Infrastructure.Messaging;

// Un consumidor de un único evento de integración. Consumer tiene que ser estable y único;
// es la clave de deduplicación junto con el id de mensaje en el Inbox.
internal interface IIntegrationEventHandler
{
    string Consumer { get; }

    string EventName { get; }

    // Prepara el efecto del consumidor sobre el DbContext compartido. El dispatcher es dueño
    // de la guarda del Inbox y el llamador es dueño de la transacción y del SaveChanges.
    Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken);
}
