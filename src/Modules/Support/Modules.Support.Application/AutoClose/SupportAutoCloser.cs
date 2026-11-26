using Modules.Support.Domain;

namespace Modules.Support.Application;

/// <summary>
/// Fase 4b, Tarea 14 (H22, owner 2026-11-16, Q-4): cierra los tickets Resueltos cuando pasan N días hábiles sin respuesta del cliente (N
/// por tenant, de 1 a 30, 5 por defecto; nunca se apaga). La fecha es el N-ésimo día hábil después de la resolución o de la última
/// respuesta del cliente que sigue en el expediente (vínculo <c>Linked</c> o <c>Pinned</c>), lo que sea más tarde, en el calendario de
/// Soporte del tenant; el ticket se cierra cuando la fecha local ya pasó esa fecha. Se recalcula siempre: soltar la respuesta devuelve la
/// fecha. El toque y el comentario de la encuesta nunca cuentan.
///
/// Cierra como el sistema: <c>ChangeStatus(Closed)</c> sin actor (la traza queda sin autor), auditoría de sistema
/// <c>support.ticket.status_changed</c> y, con conversación, <c>TicketClosed</c> en el hilo en la misma transacción. Después del commit
/// avisa el hilo y el ticket. Antes de cerrar relee el ticket: si ya no está Resuelto o se resolvió otra vez, no lo toca. Un cambio que
/// le ganó (otra réplica, un asesor) sale como <c>RequestConcurrencyException</c> y el worker lo salta.
/// </summary>
public sealed class SupportAutoCloser(
    ISupportAutoCloseFeed feed,
    ISupportTicketRepository tickets,
    SupportTicketDtoComposer composer,
    ISupportUnitOfWork unitOfWork,
    ISupportAuditPublisher auditPublisher,
    ISupportRealtime realtime,
    ISupportConversationEvents conversationEvents)
{
    public const int DefaultBatchSize = 100;

    private static readonly string[] ChangedFields = ["status"];

    /// <summary>El N-ésimo día hábil después de la resolución o de la última respuesta del cliente, lo que sea más tarde.</summary>
    public static DateOnly AutoCloseOn(ISupportBusinessCalendar calendar, DateOnly resolvedOn, DateOnly? lastReplyOn, int businessDays)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        var from = lastReplyOn is { } reply && reply > resolvedOn ? reply : resolvedOn;
        return SupportBusinessDays.Add(calendar, from, businessDays);
    }

    /// <summary>Se cierra cuando la fecha local del tenant es posterior a la del cierre.</summary>
    public static bool IsDue(DateOnly autoCloseOn, DateOnly today) => today > autoCloseOn;

    /// <summary>La fecha del cierre automático de un ticket Resuelto, con la última respuesta del cliente leída ahora; null si no está
    /// Resuelto. La usa también el detalle (<c>clock.autoCloseOn</c>).</summary>
    public static async Task<DateOnly?> ForAsync(
        ISupportAutoCloseFeed feed, SupportTicket ticket, SupportReadContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(context);
        if (ticket is not { Status: SupportTicketStatus.Resolved, ResolvedAt: { } resolvedAt })
        {
            return null;
        }

        var lastReply = await feed.LastCustomerReplyAsync(ticket.TenantId, ticket.Id.Value, resolvedAt, cancellationToken);
        return AutoCloseOn(
            context.Clock.Calendar,
            context.Clock.LocalDate(resolvedAt),
            lastReply is { } reply ? context.Clock.LocalDate(reply) : null,
            context.AutoCloseBusinessDays);
    }

    public Task<IReadOnlyList<SupportAutoCloseCandidate>> ListCandidatesAsync(
        int take, SupportAutoCloseCandidate? after, CancellationToken cancellationToken) =>
        feed.ListCandidatesAsync(take, after, cancellationToken);

    /// <summary>Cierra el ticket si le toca. Falso si no le tocaba o ya no estaba en esa resolución.</summary>
    public async Task<bool> CloseIfDueAsync(SupportAutoCloseCandidate candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (await tickets.FindAsync(candidate.TenantId, new SupportTicketId(candidate.TicketId), cancellationToken) is not
            {
                Status: SupportTicketStatus.Resolved, ResolvedAt: { } resolvedAt,
            } ticket
            || resolvedAt != candidate.ResolvedAt)
        {
            return false;
        }

        var context = await composer.ReadContextAsync(candidate.TenantId, cancellationToken);
        if (await ForAsync(feed, ticket, context, cancellationToken) is not { } closeOn || !IsDue(closeOn, context.Today))
        {
            return false;
        }

        var now = context.Now;
        ticket.ChangeStatus(SupportTicketStatus.Closed, null, now, context.Clock);
        if (ticket.ConversationId is { } conversationId)
        {
            await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
            {
                await conversationEvents.AppendAsync(
                    ticket.TenantId, conversationId, opened: false, ticket.Id.Value, null, now, cancellationToken);
                await SaveAsync(ticket, now, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }

            await conversationEvents.NotifyChangedAsync(ticket.TenantId, conversationId, cancellationToken);
        }
        else
        {
            await SaveAsync(ticket, now, cancellationToken);
        }

        await SupportTicketNotices.ChangedAsync(realtime, ticket, formerAssigneeMemberId: null, CancellationToken.None);
        return true;
    }

    private async Task SaveAsync(SupportTicket ticket, DateTimeOffset now, CancellationToken cancellationToken)
    {
        auditPublisher.PublishSystem(
            ticket.TenantId, SupportAuditActions.TicketStatusChanged, SupportAuditActions.Resources.Ticket, ticket.Id.ToString(),
            ChangedFields, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
