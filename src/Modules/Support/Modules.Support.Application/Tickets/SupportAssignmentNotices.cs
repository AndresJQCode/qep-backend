using Modules.Support.Domain;

namespace Modules.Support.Application;

/// <summary>
/// Task-notices spec 2026-11-22 D-7 for the creation paths: a customer ticket born already assigned to someone other than who created it
/// publishes <c>support.ticket.assigned.v1</c>, the same event <c>AssignTicket</c> publishes when the hand-over happens later. Without it the
/// assignee only learned about tickets assigned after the fact.
/// </summary>
public static class SupportAssignmentNotices
{
    /// <summary>
    /// Call before saving, inside the creation transaction, so the outbox row commits with the ticket. Never when the creator keeps the
    /// ticket, nor when there is no assignee. Tasks have their own event and their own call site.
    /// </summary>
    public static void PublishOnCreate(ISupportIntegrationEvents events, SupportTicket ticket, Guid actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(ticket);

        if (!ticket.IsInternal && ticket.AssigneeMemberId is { } assignee && assignee != actor)
        {
            events.PublishTicketAssigned(ticket, actor, now);
        }
    }
}
