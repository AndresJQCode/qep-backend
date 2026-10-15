using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;
using Modules.Messaging.Infrastructure.Persistence;

namespace Modules.Messaging.Infrastructure.Realtime;

/// <summary>
/// Spec 2026-10-14, decisión 6: el fanout entre pods es <c>pg_notify</c>, sin Redis. Va por el <c>MessagingDbContext</c>
/// del scope: dentro de una transacción abierta Postgres lo entrega al commit, y fuera de ella al instante. El payload son
/// ids, muy por debajo de los 8000 bytes del canal. Quien llama ya commiteó y no reintenta por esto: un fallo acá se
/// registra arriba y el polling relajado del frontend cubre el hueco.
/// </summary>
internal sealed class PgMessagingNotifier(MessagingDbContext dbContext) : IMessagingNotifier
{
    public Task ConversationChangedAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
        PublishAsync(new MessagingEvent(MessagingEventKinds.ConversationChanged, tenantId, conversationId, null, null), cancellationToken);

    public Task InboundMessageAsync(Guid tenantId, Guid conversationId, Guid messageId, Guid? assignedMemberId, CancellationToken cancellationToken) =>
        PublishAsync(new MessagingEvent(MessagingEventKinds.InboundMessage, tenantId, conversationId, messageId, assignedMemberId), cancellationToken);

    private async Task PublishAsync(MessagingEvent @event, CancellationToken cancellationToken)
    {
        var payload = MessagingEventJson.Serialize(@event);
        await dbContext.Database.ExecuteSqlAsync($"SELECT pg_notify({MessagingEventJson.Channel}, {payload})", cancellationToken);
    }
}
