using Microsoft.EntityFrameworkCore;
using Modules.Support.Application;
using Modules.Support.Domain;

namespace Modules.Support.Infrastructure.Persistence;

internal sealed class SupportTicketMessageRepository(SupportDbContext dbContext) : ISupportTicketMessageRepository
{
    public void Add(SupportTicketMessage link) => dbContext.TicketMessages.Add(link);

    public Task<SupportTicketMessage?> FindAsync(
        Guid tenantId, SupportTicketId ticketId, Guid messageId, CancellationToken cancellationToken) =>
        dbContext.TicketMessages.FirstOrDefaultAsync(
            link => link.TenantId == tenantId && link.TicketId == ticketId && link.MessageId == messageId, cancellationToken);

    // El orden y el filtro son los de IX_ticket_messages_ticket_visible: (occurred_at, message_id) DESC sin los soltados.
    public async Task<IReadOnlyList<SupportTicketMessage>> ListVisibleAsync(
        Guid tenantId,
        SupportTicketId ticketId,
        (DateTimeOffset OccurredAt, Guid MessageId)? before,
        int take,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);
        var links = dbContext.TicketMessages
            .AsNoTracking()
            .Where(link => link.TenantId == tenantId
                && link.TicketId == ticketId
                && link.State != SupportTicketMessageState.Released);
        // Comparación de fila (occurred_at, message_id) < (…), como MessageQueries.ListThreadAsync de Mensajería.
        if (before is var (occurredAt, messageId))
        {
            links = links.Where(link => EF.Functions.LessThan(
                ValueTuple.Create(link.OccurredAt, link.MessageId), ValueTuple.Create(occurredAt, messageId)));
        }

        return await links
            .OrderByDescending(link => link.OccurredAt)
            .ThenByDescending(link => link.MessageId)
            .Take(take)
            .ToListAsync(cancellationToken);
    }
}
