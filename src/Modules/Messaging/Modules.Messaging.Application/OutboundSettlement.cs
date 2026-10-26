using BuildingBlocks.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>
/// §8.3, la tabla de respuestas de Meta, extraída de <see cref="OutboundGuards"/> sin cambiar su comportamiento para que el envío
/// del bot (<see cref="BotOutbound"/>, spec 2026-10-21 D-I4) la comparta sin <c>IExecutionContext</c>: cierra el reclamo, avisa y
/// quita la marca de no leído igual que los envíos de un asesor.
/// </summary>
internal sealed class OutboundSettlement(
    IConversationRepository repository,
    IMessagingConnectionDirectory connections,
    IMessagingNotifier notifier,
    ReadReceipts receipts,
    IClock clock)
{
    /// <summary>
    /// §8.3, la tabla de respuestas. Vuelve sólo si quedó <c>Sent</c>; en cualquier otro caso cierra el reclamo y lanza.
    /// Todo cierre va con <c>CancellationToken.None</c>: si Meta ya aceptó, un rollback por cancelación borraría la fila
    /// y el reintento con el mismo clientId lo mandaría dos veces. <paramref name="afterRollback"/> corre después de un
    /// rollback por credencial (la nota de voz borra ahí lo que subió a R2). <paramref name="seen"/> es el <c>unread_count</c> que
    /// tenía la conversación al cargarla en este request: lo que el asesor tenía a la vista al responder (spec 2026-10-16 §3.1).
    /// </summary>
    public async Task SettleAsync(
        Guid tenantId, Guid connectionId, IOutboundClaim claim, SendResult result, DateTimeOffset answeredAt, string? metaMediaId, Func<Task>? afterRollback,
        int seen)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(result);
        switch (result.Outcome)
        {
            case SendOutcome.Sent when !string.IsNullOrEmpty(result.Wamid):
                await claim.CommitSentAsync(result.Wamid, answeredAt, metaMediaId, CancellationToken.None);
                // Spec 2026-10-16 §3.1: responder quita la marca. Va antes del aviso para que ese único aviso ya lleve el contador nuevo.
                var replied = await MarkRepliedAsync(tenantId, claim.ConversationId, seen);
                await NotifyConsolidatedAsync(tenantId, claim.ConversationId);
                // Sólo con el contador en 0: si entró algo durante el envío, el acuse de WhatsApp sobre el último wamid lo marcaría
                // leído también, y en Instagram mark_seen es por persona. Se manda cuando alguien quite la marca después.
                if (replied is { RemainingUnread: 0 })
                {
                    await receipts.SendAsync(tenantId, claim.ConversationId, replied.Receipt, clock.UtcNow, CancellationToken.None);
                }

                return;

            case SendOutcome.GraphError when result.Code is MetaErrorCodes.TokenExpired or MetaErrorCodes.NumberUnregistered:
                // §8.3: la credencial o el número ya no sirven; la fila no se guarda y la conexión pasa a NeedsAttention.
                await claim.RollbackAsync(CancellationToken.None);
                if (afterRollback is not null)
                {
                    await afterRollback();
                }

                await ConnectionRejection.ReportAsync(connections, tenantId, connectionId, result.Code.Value);
                throw new MessagingDomainException(MessagingErrorCodes.ConnectionUnavailable, "Meta rejected the connection credentials.");

            case SendOutcome.GraphError when result.Code is MetaErrorCodes.CustomerWindowClosed:
                await claim.CommitFailedAsync(MetaErrorCodes.CustomerWindowClosed, result.Title, answeredAt, metaMediaId, CancellationToken.None);
                await NotifyConsolidatedAsync(tenantId, claim.ConversationId);
                throw new MessagingDomainException(MessagingErrorCodes.WindowClosed, "Meta closed the 24-hour window.");

            case SendOutcome.GraphError when result.Code is MetaErrorCodes.InstagramOutsideWindow:
                // Spec 2026-10-14 §6.2: en Instagram «fuera de la ventana» viaja con el código genérico de permiso (10) y el
                // subcódigo 2534022; el cliente ya lo distinguió por subcódigo. La fila queda Failed con el 10, que la tabla traduce.
                await claim.CommitFailedAsync(result.Code.Value, result.Title, answeredAt, metaMediaId, CancellationToken.None);
                throw new MessagingDomainException(MessagingErrorCodes.WindowClosed, "Instagram closed the 24-hour window.");

            case SendOutcome.GraphError:
                await claim.CommitFailedAsync(result.Code ?? SendMessageHandler.UnknownGraphErrorCode, result.Title, answeredAt, metaMediaId, CancellationToken.None);
                await NotifyConsolidatedAsync(tenantId, claim.ConversationId);
                throw new MessagingDomainException(MessagingErrorCodes.MessageRejected, "Meta rejected the message.");

            default:
                // Timeout, 5xx, red o un 2xx sin messages[0].id: Meta pudo haberlo aceptado (riesgo residual de §8.3).
                await claim.CommitFailedAsync(MessageFailureReasons.Unconfirmed, null, answeredAt, metaMediaId, CancellationToken.None);
                await NotifyConsolidatedAsync(tenantId, claim.ConversationId);
                throw new MessagingDomainException(MessagingErrorCodes.MessageRejected, "The send could not be confirmed with Meta.");
        }
    }

    /// <summary>Spec 2026-10-14: Sent o Failed quedaron commiteados, así que el otro asesor del hilo tiene algo que leer. Un
    /// rollback no avisa. Un fallo del aviso no deshace el envío ni cambia la respuesta: Meta ya aceptó, y el polling cubre.</summary>
    private async Task NotifyConsolidatedAsync(Guid tenantId, Guid conversationId)
    {
        try
        {
            await notifier.ConversationChangedAsync(tenantId, conversationId, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Application no tiene logger; la causa no se pierde del todo: el listener del otro lado registra la suya.
            _ = exception;
        }
    }

    /// <summary>Spec 2026-10-16 §3.1: resta lo que el asesor vio, no lo pone en 0, para que un entrante que llegó durante el envío
    /// sobreviva. Un fallo se traga como el aviso: Meta ya aceptó y la respuesta del envío no cambia; la marca queda y se quita a
    /// mano.</summary>
    private async Task<RepliedTarget?> MarkRepliedAsync(Guid tenantId, Guid conversationId, int seen)
    {
        try
        {
            return await repository.MarkRepliedAsync(tenantId, conversationId, seen, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Como en NotifyConsolidatedAsync, aquí no hay logger. Sin resta tampoco sale acuse: a Meta nunca le llega un leído que la
            // base no refleja.
            _ = exception;
            return null;
        }
    }
}
