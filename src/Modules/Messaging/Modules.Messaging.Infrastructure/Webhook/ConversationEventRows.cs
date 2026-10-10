using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Persistence;

namespace Modules.Messaging.Infrastructure.Webhook;

/// <summary>Spec 2026-10-10 §6.1.5 y §8.7: un evento es una fila de messages con direction = 3, kind = 13, status = 2
/// (D-A5), sin wamid, client_id, text ni caption (su search_vector sale vacío solo). Va en la transacción de quien lo
/// causa; nunca toca la conversación.</summary>
internal static class ConversationEventRows
{
    public static Task InsertAsync(
        MessagingDbContext dbContext, Guid tenantId, Guid connectionId, Guid conversationId,
        ConversationEvent value, DateTimeOffset occurredAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var details = ConversationEventJson.Serialize(value);
        return dbContext.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO messaging.messages (id, conversation_id, tenant_id, connection_id, occurred_at, direction, kind, status, details, created_at)
            VALUES ({Guid.CreateVersion7()}, {conversationId}, {tenantId}, {connectionId}, {occurredAt.ToUniversalTime()}, 3, 13, 2, {details}::jsonb, {now})
            """,
            cancellationToken);
    }
}
