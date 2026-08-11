using Microsoft.EntityFrameworkCore;
using BuildingBlocks.Application;
using Modules.Tenancy.Infrastructure.Persistence;

namespace Modules.Tenancy.Infrastructure.Messaging;

internal interface IIntegrationEventDispatcher
{
    Task<int> DispatchAsync(OutboxMessage message, CancellationToken cancellationToken);
}

// Rutea un mensaje del Outbox a cada handler que consume su evento y protege a
// cada handler con una fila de Inbox con clave (consumidor, id de mensaje). Un mensaje
// reentregado encuentra la fila de Inbox ya presente y saltea el efecto, así que el
// consumidor queda idempotente bajo entrega at-least-once.
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
