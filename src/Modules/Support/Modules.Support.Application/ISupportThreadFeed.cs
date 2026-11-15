namespace Modules.Support.Application;

/// <summary>Un entrante que algún ticket de su conversación podría tomar (plan E2): uno en vivo sin decidir, o una fila sin clasificar que
/// quedó con un solo candidato.</summary>
public sealed record SupportPendingInbound(Guid TenantId, Guid ConversationId, Guid MessageId, DateTimeOffset OccurredAt);

/// <summary>
/// Lo que el seguimiento del hilo lee en cada pasada (plan E2): Mensajería no empuja nada, Soporte sondea. El adaptador de Bootstrapper
/// es SQL crudo sobre <c>support.tickets</c>, <c>support.ticket_messages</c>, <c>support.unclassified_messages</c> y
/// <c>messaging.messages</c>, de todos los tenants.
/// </summary>
public interface ISupportThreadFeed
{
    /// <summary>Entrantes en vivo (<c>direction = 1</c> y no importados) de conversaciones con algún ticket no cerrado cuya ventana de
    /// seguimiento los cubre, sin fila en <c>ticket_messages</c> (de ningún ticket) ni en <c>unclassified_messages</c>. Por
    /// <c>(occurred_at, message_id)</c>, de a <paramref name="take"/>. Un ticket que apagó «Seguir el hilo» cuenta sólo un rato después
    /// del apagado (el adaptador fija cuánto).</summary>
    Task<IReadOnlyList<SupportPendingInbound>> ListPendingAsync(int take, CancellationToken cancellationToken);

    /// <summary>Las filas sin clasificar pendientes que hoy tienen exactamente un ticket abierto que las cubre (plan E5): las que esperan
    /// al asesor o se quedaron sin candidatos no salen, así nunca ocupan el lote. Por <c>(occurred_at, message_id)</c>.</summary>
    Task<IReadOnlyList<SupportPendingInbound>> ListReassignableUnclassifiedAsync(int take, CancellationToken cancellationToken);
}
