using Modules.Tenancy.Infrastructure.Persistence;

namespace Modules.Tenancy.Infrastructure.Messaging;

// A consumer of a single integration event. Consumer must be stable and unique;
// it is the dedupe key together with the message id in the Inbox.
internal interface IIntegrationEventHandler
{
    string Consumer { get; }

    string EventName { get; }

    // Stages the consumer's effect on the shared DbContext. The dispatcher owns
    // the Inbox guard and the caller owns the transaction and SaveChanges.
    Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken);
}
