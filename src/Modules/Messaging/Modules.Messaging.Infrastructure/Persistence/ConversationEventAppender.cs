using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Webhook;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>Soporte fase 2 (plan E11): la misma fila de evento que escribe la ingesta (<see cref="ConversationEventRows"/>), sobre la
/// conexión y la transacción que tenga el <see cref="MessagingDbContext"/> del scope. No abre transacción propia ni toca la conversación.</summary>
internal sealed class ConversationEventAppender(MessagingDbContext dbContext, IClock clock) : IConversationEventAppender
{
    public async Task<bool> AppendTicketEventAsync(
        Guid tenantId, Guid conversationId, ConversationEventType type, Guid ticketId, Guid? actorMemberId, DateTimeOffset occurredAt,
        CancellationToken ct)
    {
        if (type is not (ConversationEventType.TicketOpened or ConversationEventType.TicketClosed))
        {
            throw new ArgumentException("Only TicketOpened and TicketClosed can be appended from Support.", nameof(type));
        }

        var connectionId = await dbContext.Conversations.AsNoTracking()
            .Where(conversation => conversation.TenantId == tenantId && conversation.Id == conversationId)
            .Select(conversation => (Guid?)conversation.ConnectionId)
            .FirstOrDefaultAsync(ct);
        if (connectionId is not { } connection)
        {
            return false;
        }

        await ConversationEventRows.InsertAsync(
            dbContext, tenantId, connection, conversationId,
            new ConversationEvent(type, Actor: actorMemberId, TicketId: ticketId), occurredAt, clock.UtcNow, ct);
        return true;
    }
}
