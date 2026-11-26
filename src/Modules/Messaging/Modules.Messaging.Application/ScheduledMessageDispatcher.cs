using BuildingBlocks.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>
/// Spec 2026-11-09 «Worker» y D9: lo que el worker de programados hace además de ejecutar. Reclama los vencidos (con un lease real: lo escribe
/// el SQL del store, no <c>ScheduledMessage.MarkSending</c>), recupera los <c>Sending</c> de lease vencido y purga los adjuntos que una fila
/// terminal no pudo borrar.
/// <para>
/// La recuperación <b>cierra</b> la fila, nunca la reprograma ni la reejecuta: reenviar podría mandar el mensaje dos veces. Mira la tabla de
/// mensajes con el <c>clientId</c> y cierra con eso (<c>Sent</c>, o <c>message.rejected</c> sólo si la fila guarda un código de Meta) o como
/// <c>unknown_outcome</c>: sin fila, o con un <c>Failed</c> que Meta no confirmó (<see cref="MessageFailureReasons.Unconfirmed"/>) o sin código.
/// Decisión del owner (2026-11-10): llamar rechazado a algo que quizá salió invita a un reintento que duplica el mensaje. El cierre es el
/// compare-and-set de siempre, así que si el worker original termina tarde su cierre no escribe nada.
/// </para>
/// </summary>
public sealed class ScheduledMessageDispatcher(
    IScheduledMessageStore store, IOutboundMessages outbound, IMessagingMediaStore media, IMessagingNotifier notifier, IClock clock)
{
    public static readonly TimeSpan LeaseLength = TimeSpan.FromMinutes(10);

    public const int BatchSize = 20;

    public Task<IReadOnlyList<ClaimedScheduledMessage>> ClaimDueAsync(CancellationToken cancellationToken) =>
        store.ClaimDueAsync(BatchSize, clock.UtcNow, LeaseLength, cancellationToken);

    /// <summary>Devuelve cuántas filas cerró; un cierre que el CAS no aceptó (el original llegó antes) no cuenta ni avisa.</summary>
    public async Task<int> RecoverExpiredAsync(CancellationToken cancellationToken)
    {
        var expired = await store.ClaimExpiredAsync(BatchSize, clock.UtcNow, LeaseLength, cancellationToken);
        var recovered = 0;
        foreach (var message in expired)
        {
            var existing = await outbound.FindByClientIdAsync(message.ConversationId, message.ClientId, cancellationToken);
            var closed = existing switch
            {
                { Status: MessageStatus.Sent or MessageStatus.Delivered or MessageStatus.Read } =>
                    await store.CompleteSentAsync(message, existing.Id, clock.UtcNow, CancellationToken.None),
                { Status: MessageStatus.Failed, FailureCode: { } code } when code != MessageFailureReasons.Unconfirmed =>
                    await store.CompleteFailedAsync(message, MessagingErrorCodes.MessageRejected, clock.UtcNow, CancellationToken.None),
                _ => await store.CompleteFailedAsync(message, MessagingErrorCodes.ScheduledUnknownOutcome, clock.UtcNow, CancellationToken.None),
            };

            if (!closed)
            {
                continue;
            }

            recovered++;
            await NotifyAsync(message);
        }

        return recovered;
    }

    /// <summary>Borra el objeto (best effort: un fallo se traga) y limpia la clave, para que la fila deje de listarse.</summary>
    public async Task<int> PurgeAsync(CancellationToken cancellationToken)
    {
        var purgeable = await store.ListPurgeableAsync(BatchSize, cancellationToken);
        foreach (var item in purgeable)
        {
            try
            {
                await media.DeleteAsync(item.StorageKey, cancellationToken);
            }
            catch (Exception exception) when (exception is not (OutOfMemoryException or OperationCanceledException))
            {
                // Un objeto que no se pudo borrar queda huérfano hasta el lifecycle del bucket; la fila no se reintenta para siempre.
            }

            await store.ClearMediaKeyAsync(item.TenantId, item.Id, cancellationToken);
        }

        return purgeable.Count;
    }

    // D11: el aviso no cambia el cierre ya escrito; el polling del SPA cubre.
    private async Task NotifyAsync(ClaimedScheduledMessage message)
    {
        try
        {
            await notifier.ConversationChangedAsync(message.TenantId, message.ConversationId, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Ver comentario.
        }
    }
}
