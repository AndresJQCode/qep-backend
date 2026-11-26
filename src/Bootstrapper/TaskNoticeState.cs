using Modules.Notifications.Application;
using Modules.Support.Application;

namespace Bootstrapper;

/// <summary>Task-notices spec 2026-11-22 §5.4: the escalation worker reads the ticket behind a bell entry through Support's narrow reader.
/// Notifications does not reference Support, so the mapping lives here, one to one.</summary>
internal sealed class TaskNoticeState(ISupportTaskNoticeReader reader) : ITaskNoticeState
{
    public async Task<TaskNoticeTicket?> FindAsync(Guid tenantId, Guid ticketId, CancellationToken cancellationToken)
    {
        var snapshot = await reader.FindAsync(tenantId, ticketId, cancellationToken);
        return snapshot is null
            ? null
            : new TaskNoticeTicket(
                snapshot.IsInternal,
                snapshot.IsOpen,
                snapshot.AssigneeMemberId,
                snapshot.TicketNumber,
                snapshot.Subject,
                snapshot.WhatsAppConnectionId);
    }
}
