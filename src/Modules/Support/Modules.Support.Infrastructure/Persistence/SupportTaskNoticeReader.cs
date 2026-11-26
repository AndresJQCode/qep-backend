using Microsoft.EntityFrameworkCore;
using Modules.Support.Application;
using Modules.Support.Domain;

namespace Modules.Support.Infrastructure.Persistence;

/// <summary>Task-notices spec 2026-11-22 §5.7: the read the escalation worker makes right before it sends. Two projections, no tracking.</summary>
internal sealed class SupportTaskNoticeReader(SupportDbContext dbContext) : ISupportTaskNoticeReader
{
    public async Task<SupportTaskNoticeSnapshot?> FindAsync(Guid tenantId, Guid ticketId, CancellationToken cancellationToken)
    {
        var id = new SupportTicketId(ticketId);
        var ticket = await dbContext.Tickets
            .AsNoTracking()
            .Where(candidate => candidate.TenantId == tenantId && candidate.Id == id)
            .Select(candidate => new
            {
                candidate.IsInternal,
                candidate.Status,
                candidate.AssigneeMemberId,
                candidate.Number,
                candidate.Subject,
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (ticket is null)
        {
            return null;
        }

        // A tenant that never saved the settings has no row (or no choice): no connection, the notice goes by email.
        var connectionId = await dbContext.TenantSettings
            .AsNoTracking()
            .Where(settings => settings.TenantId == tenantId)
            .Select(settings => settings.TaskNoticeConnectionId)
            .FirstOrDefaultAsync(cancellationToken);

        return new SupportTaskNoticeSnapshot(
            ticket.IsInternal,
            ticket.Status is not (SupportTicketStatus.Resolved or SupportTicketStatus.Closed),
            ticket.AssigneeMemberId,
            SupportTicketNumber.Format(ticket.Number),
            ticket.Subject,
            connectionId);
    }
}
