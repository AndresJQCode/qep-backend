using BuildingBlocks.Application;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>La misma fila que <c>ConversationEventRows</c> (ingesta, SQL), por EF para los handlers. Spec 2026-11-22 D-10: si el evento asigna
/// la conversación a alguien, el aviso de la campana va al outbox en el mismo <c>SaveChanges</c>.</summary>
internal sealed class ConversationEvents(MessagingDbContext dbContext, IClock clock) : IConversationEvents
{
    public void Record(Guid tenantId, Guid connectionId, Guid conversationId, ConversationEvent value, DateTimeOffset occurredAt)
    {
        dbContext.Messages.Add(new MessageRecord
        {
            Id = Guid.CreateVersion7(),
            ConversationId = conversationId,
            TenantId = tenantId,
            ConnectionId = connectionId,
            OccurredAt = occurredAt.ToUniversalTime(),
            Direction = MessageDirection.System,
            Kind = MessageKind.Event,
            Status = MessageStatus.Delivered,
            Details = ConversationEventJson.Serialize(value),
            CreatedAt = occurredAt.ToUniversalTime(),
        });
        ConversationAssignmentOutbox.Add(dbContext, tenantId, conversationId, value, occurredAt, clock.UtcNow);
    }
}
