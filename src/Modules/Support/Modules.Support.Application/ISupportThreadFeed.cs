namespace Modules.Support.Application;

/// <summary>Un entrante en vivo que algún ticket de su conversación podría tomar (plan E2).</summary>
public sealed record SupportPendingInbound(Guid TenantId, Guid ConversationId, Guid MessageId, DateTimeOffset OccurredAt);

/// <summary>Una conversación con entrantes sin clasificar pendientes.</summary>
public sealed record SupportConversationKey(Guid TenantId, Guid ConversationId);

/// <summary>
/// Lo que el seguimiento del hilo lee en cada pasada (plan E2): Mensajería no empuja nada, Soporte sondea. El adaptador de Bootstrapper
/// es SQL crudo sobre <c>support.tickets</c>, <c>support.ticket_messages</c>, <c>support.unclassified_messages</c> y
/// <c>messaging.messages</c>, de todos los tenants.
/// </summary>
public interface ISupportThreadFeed
{
    /// <summary>Entrantes en vivo (<c>direction = 1</c> y no importados) de conversaciones con algún ticket no cerrado cuya ventana de
    /// seguimiento los cubre, sin fila en <c>ticket_messages</c> (de ningún ticket) ni en <c>unclassified_messages</c>. Por
    /// <c>(occurred_at, message_id)</c>, de a <paramref name="take"/>.</summary>
    Task<IReadOnlyList<SupportPendingInbound>> ListPendingAsync(int take, CancellationToken cancellationToken);

    /// <summary>Las conversaciones con filas sin clasificar pendientes: el seguimiento revisa si alguna quedó con un solo candidato.</summary>
    Task<IReadOnlyList<SupportConversationKey>> ListConversationsWithPendingUnclassifiedAsync(int take, CancellationToken cancellationToken);
}
