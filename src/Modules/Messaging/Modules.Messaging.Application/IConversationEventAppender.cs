using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>
/// Soporte fase 2 (plan E11): escribe en el hilo de una conversación los eventos <c>TicketOpened</c> y <c>TicketClosed</c>. Lo consume
/// Soporte por un adaptador de Bootstrapper que enrola el <c>MessagingDbContext</c> del scope en la transacción de Soporte: quien llama
/// decide la transacción, y así no queda un ticket cerrado sin su evento ni al revés. El evento es una marca: no toca la versión, el
/// estado, los no leídos ni <c>history_managed</c> de la conversación.
/// </summary>
public interface IConversationEventAppender
{
    /// <summary><c>false</c>, sin escribir nada, si la conversación no existe o es de otro tenant. Sólo acepta <c>TicketOpened</c> o
    /// <c>TicketClosed</c>; otro tipo es <see cref="ArgumentException"/>.</summary>
    Task<bool> AppendTicketEventAsync(
        Guid tenantId, Guid conversationId, ConversationEventType type, Guid ticketId, Guid? actorMemberId, DateTimeOffset occurredAt,
        CancellationToken ct);
}
