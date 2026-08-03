using Microsoft.EntityFrameworkCore;
using BuildingBlocks.Application;
using Modules.Tenancy.Infrastructure.Persistence;

namespace Modules.Tenancy.Infrastructure.Messaging;

internal interface IIntegrationEventDispatcher
{
    Task<int> DispatchAsync(OutboxMessage message, CancellationToken cancellationToken);
}

// Routes an Outbox message to every handler that consumes its event and guards
// each handler with an Inbox row keyed by (consumer, message id). A redelivered
// message finds the Inbox row already present and skips the effect, so the
// consumer stays idempotent under at-least-once delivery.
internal sealed class IntegrationEventDispatcher(
    TenancyDbContext dbContext,
    IEnumerable<IIntegrationEventHandler> handlers,
    IClock clock) : IIntegrationEventDispatcher
{
    public async Task<int> DispatchAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var applied = 0;

        foreach (var handler in handlers)
        {
            if (!string.Equals(handler.EventName, message.EventName, StringComparison.Ordinal))
            {
                continue;
            }

            var alreadyHandled = await dbContext.Set<InboxMessage>().AnyAsync(
                entry => entry.Consumer == handler.Consumer && entry.MessageId == message.Id,
                cancellationToken);
            if (alreadyHandled)
            {
                continue;
            }

            await handler.HandleAsync(message, cancellationToken);
            dbContext.Set<InboxMessage>().Add(new InboxMessage
            {
                Consumer = handler.Consumer,
                MessageId = message.Id,
                ProcessedAt = clock.UtcNow
            });
            applied++;
        }

        return applied;
    }
}
