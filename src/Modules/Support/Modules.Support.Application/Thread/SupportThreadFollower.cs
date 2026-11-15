using BuildingBlocks.Application;
using Modules.Support.Domain;

namespace Modules.Support.Application;

/// <summary>
/// El seguimiento del hilo (spec §8.2, plan E2 a E6): en cada pasada toma los entrantes en vivo que el feed le da y decide. Un solo
/// ticket abierto de la conversación cuya ventana cubre la hora del mensaje: lo vincula (<see cref="SupportTicketMessageState.Linked"/>,
/// sin evento ni versión) y, si el ticket esperaba al cliente, lo pasa a <see cref="SupportTicketStatus.Open"/> como sistema. Dos o
/// más: el entrante queda sin clasificar. Ninguno (el ticket se cerró entre el feed y la carga): nada. Después revisa las conversaciones
/// con pendientes sin clasificar y pasa sola cada fila que quedó con un único candidato.
///
/// Cada entrante va en su propio guardado. Es idempotente por índices, no por candados (plan E3): si otra réplica ya vinculó o registró
/// el mismo mensaje, el guardado choca con <c>IX_ticket_messages_ticket_message</c> o <c>PK_unclassified_messages</c> y se descarta; si
/// otra escritura cambió el ticket (versión), el entrante se queda para la siguiente pasada. Entre uno y otro se olvida lo cargado
/// (<see cref="ISupportUnitOfWork.DiscardChanges"/>), así un guardado fallido no se vuelve a mandar con el siguiente.
/// </summary>
public sealed class SupportThreadFollower(
    ISupportThreadFeed feed,
    ISupportTicketRepository tickets,
    ISupportTicketMessageRepository links,
    ISupportUnclassifiedRepository unclassified,
    ISupportUnitOfWork unitOfWork,
    ISupportAuditPublisher auditPublisher,
    SupportTicketDtoComposer composer)
{
    /// <summary>Entrantes por pasada cuando quien llama no dice otro (<c>Support:Workers:BatchSize</c>).</summary>
    public const int DefaultBatchSize = 200;

    public Task<int> ProcessBatchAsync(CancellationToken cancellationToken) => ProcessBatchAsync(DefaultBatchSize, cancellationToken);

    /// <summary>Devuelve cuántos entrantes resolvió en esta pasada (vinculados, registrados sin clasificar o pasados solos).</summary>
    public async Task<int> ProcessBatchAsync(int batchSize, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        var resolved = 0;
        foreach (var inbound in await feed.ListPendingAsync(batchSize, cancellationToken))
        {
            if (await TryAsync(() => FollowAsync(inbound, cancellationToken)))
            {
                resolved++;
            }
        }

        foreach (var conversation in await feed.ListConversationsWithPendingUnclassifiedAsync(batchSize, cancellationToken))
        {
            var pending = await unclassified.ListPendingAsync(conversation.TenantId, conversation.ConversationId, cancellationToken);
            var messageIds = pending.Select(row => row.MessageId).ToList();
            unitOfWork.DiscardChanges();
            foreach (var messageId in messageIds)
            {
                if (await TryAsync(() => ReassignAsync(conversation.TenantId, messageId, cancellationToken)))
                {
                    resolved++;
                }
            }
        }

        return resolved;
    }

    private async Task<bool> FollowAsync(SupportPendingInbound inbound, CancellationToken cancellationToken)
    {
        var candidates = (await tickets.ListOpenByConversationAsync(inbound.TenantId, inbound.ConversationId, cancellationToken))
            .Where(ticket => ticket.CoversInbound(inbound.OccurredAt))
            .ToList();
        if (candidates.Count == 0)
        {
            return false;
        }

        var context = await composer.ReadContextAsync(inbound.TenantId, cancellationToken);
        if (candidates.Count == 1)
        {
            Link(candidates[0], inbound.ConversationId, inbound.MessageId, inbound.OccurredAt, context);
        }
        else
        {
            unclassified.Add(SupportUnclassifiedMessage.Create(
                inbound.TenantId, inbound.ConversationId, inbound.MessageId, inbound.OccurredAt, context.Now));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }

    // Plan E5: la fila pendiente pasa sola cuando queda exactamente un candidato (el otro se cerró o dejó de cubrirla).
    private async Task<bool> ReassignAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken)
    {
        var row = await unclassified.FindAsync(tenantId, messageId, cancellationToken);
        if (row is not { IsPending: true })
        {
            return false;
        }

        var candidates = (await tickets.ListOpenByConversationAsync(tenantId, row.ConversationId, cancellationToken))
            .Where(ticket => ticket.CoversInbound(row.OccurredAt))
            .ToList();
        if (candidates.Count != 1)
        {
            return false;
        }

        var ticket = candidates[0];
        var context = await composer.ReadContextAsync(tenantId, cancellationToken);
        row.Assign(ticket.Id, null, context.Now);
        if (await links.FindAsync(tenantId, ticket.Id, messageId, cancellationToken) is null)
        {
            Link(ticket, row.ConversationId, messageId, row.OccurredAt, context);
        }
        else
        {
            SupportThreadRules.ReopenIfWaiting(ticket, context, auditPublisher);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }

    private void Link(SupportTicket ticket, Guid conversationId, Guid messageId, DateTimeOffset occurredAt, SupportReadContext context)
    {
        links.Add(SupportTicketMessage.Link(
            ticket.TenantId, ticket.Id, conversationId, messageId, occurredAt, SupportTicketMessageState.Linked, context.Now));
        SupportThreadRules.ReopenIfWaiting(ticket, context, auditPublisher);
    }

    // Lo que otra réplica u otra escritura ya ganó no es una falla: se descarta, o se reintenta en la siguiente pasada.
    private async Task<bool> TryAsync(Func<Task<bool>> step)
    {
        try
        {
            return await step();
        }
        catch (ResourceConflictException conflict) when (conflict.Code is SupportErrorCodes.MessageAlreadyLinked
            or SupportErrorCodes.UnclassifiedAlreadyRecorded)
        {
            return false;
        }
        catch (RequestConcurrencyException)
        {
            return false;
        }
        finally
        {
            unitOfWork.DiscardChanges();
        }
    }
}
