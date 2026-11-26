using BuildingBlocks.Application;
using Microsoft.Extensions.Logging;
using Modules.Support.Domain;

namespace Modules.Support.Application;

/// <summary>
/// El seguimiento del hilo (spec §8.2, plan E2 a E6): en cada pasada toma los entrantes en vivo que el feed le da y decide. Un solo
/// ticket abierto de la conversación cuya ventana cubre la hora del mensaje: lo vincula (<see cref="SupportTicketMessageState.Linked"/>,
/// sin evento ni versión) y, si el ticket esperaba al cliente, lo pasa a <see cref="SupportTicketStatus.Open"/> como sistema. Dos o
/// más: el entrante queda sin clasificar. Ninguno (el ticket se cerró entre el feed y la carga): nada. Después pasa sola cada fila sin
/// clasificar que el feed da como reasignable (hoy con un único candidato).
///
/// Cada entrante va en su propio guardado. Es idempotente por índices, no por candados (plan E3): si otra réplica ya vinculó o registró
/// el mismo mensaje, el guardado choca con <c>IX_ticket_messages_ticket_message</c> o <c>PK_unclassified_messages</c> y se descarta; si
/// otra escritura cambió el ticket (versión), el entrante se queda para la siguiente pasada. Entre uno y otro se olvida lo cargado
/// (<see cref="ISupportUnitOfWork.DiscardChanges"/>), así un guardado fallido no se vuelve a mandar con el siguiente.
/// </summary>
public sealed partial class SupportThreadFollower(
    ISupportThreadFeed feed,
    ISupportTicketRepository tickets,
    ISupportTicketMessageRepository links,
    ISupportUnclassifiedRepository unclassified,
    ISupportUnitOfWork unitOfWork,
    ISupportAuditPublisher auditPublisher,
    ISupportIntegrationEvents integrationEvents,
    ISupportRealtime realtime,
    SupportTicketDtoComposer composer,
    ILogger<SupportThreadFollower> logger)
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
            if (await TryAsync(inbound, () => FollowAsync(inbound, cancellationToken)))
            {
                resolved++;
            }
        }

        foreach (var row in await feed.ListReassignableUnclassifiedAsync(batchSize, cancellationToken))
        {
            if (await TryAsync(row, () => ReassignAsync(row.TenantId, row.MessageId, cancellationToken)))
            {
                resolved++;
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
        // Plan N13: the linked ticket (even without a version bump), or every candidate of the new unclassified row.
        await AnnounceAsync(candidates);
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
            SupportThreadRules.ReopenIfWaiting(ticket, context, auditPublisher, integrationEvents, actorMemberId: null);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await AnnounceAsync([ticket]);
        return true;
    }

    // After the save, with no token: a notice is never worth failing the pass, and ISupportRealtime never throws.
    private async Task AnnounceAsync(IReadOnlyList<SupportTicket> changed)
    {
        foreach (var ticket in changed)
        {
            await realtime.TicketChangedAsync(ticket.TenantId, ticket.Id.Value, CancellationToken.None);
        }
    }

    private void Link(SupportTicket ticket, Guid conversationId, Guid messageId, DateTimeOffset occurredAt, SupportReadContext context)
    {
        links.Add(SupportTicketMessage.Link(
            ticket.TenantId, ticket.Id, conversationId, messageId, occurredAt, SupportTicketMessageState.Linked, context.Now));
        SupportThreadRules.ReopenIfWaiting(ticket, context, auditPublisher, integrationEvents, actorMemberId: null);
    }

    // Lo que otra réplica u otra escritura ya ganó no es una falla: se descarta, o se reintenta en la siguiente pasada. Cualquier otra
    // falla de un entrante se registra y se salta: un mensaje que siempre revienta no puede frenar el resto del lote.
    private async Task<bool> TryAsync(SupportPendingInbound inbound, Func<Task<bool>> step)
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
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogInboundFailed(logger, exception, inbound.TenantId, inbound.ConversationId, inbound.MessageId);
            return false;
        }
        finally
        {
            unitOfWork.DiscardChanges();
        }
    }

    // Sólo ids: ni el texto del mensaje ni datos del contacto.
    [LoggerMessage(Level = LogLevel.Error, Message = "Following message {MessageId} of conversation {ConversationId} of tenant {TenantId} failed; it is skipped in this pass.")]
    private static partial void LogInboundFailed(ILogger logger, Exception exception, Guid tenantId, Guid conversationId, Guid messageId);
}
