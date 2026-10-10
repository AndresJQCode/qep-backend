using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-10 §8.7: un evento escrito por un handler; se commitea con el agregado en el mismo
/// <c>SaveChanges</c>. No toca la conversación (ni contadores ni foto).</summary>
public interface IConversationEvents
{
    void Record(Guid tenantId, Guid connectionId, Guid conversationId, ConversationEvent value, DateTimeOffset occurredAt);
}
