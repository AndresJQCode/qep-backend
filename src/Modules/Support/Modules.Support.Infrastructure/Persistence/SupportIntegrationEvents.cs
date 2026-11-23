using System.Text.Json;
using Modules.Support.Application;
using Modules.Support.Domain;

namespace Modules.Support.Infrastructure.Persistence;

/// <summary>
/// Spec 2026-11-22 §12: the events of Support for other modules. Each one is added to the outbox projection of <see cref="SupportDbContext"/>,
/// so it is saved by the same <c>SaveChanges</c> as the ticket change that causes it and never exists without it. The outbox row carries the
/// time of the write (what the 24-hour rule of the consumers looks at); the payload carries the time of the fact.
/// </summary>
internal sealed class SupportIntegrationEvents(SupportDbContext dbContext) : ISupportIntegrationEvents
{
    internal const string CustomerRepliedEvent = "support.ticket.customer-replied.v1";
    internal const string UnassignedAlertEvent = "support.ticket.unassigned-alert.v1";
    internal const string TaskAssignedEvent = "support.ticket.task-assigned.v1";

    public void PublishCustomerReplied(SupportTicket ticket, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        if (ticket.AssigneeMemberId is not { } assignee)
        {
            throw new InvalidOperationException("A customer reply is told to the assignee, and this ticket has none.");
        }

        // camelCase names are the contract with Notifications, which reads them by name.
        Add(
            CustomerRepliedEvent,
            ticket,
            occurredAt,
            new
            {
                tenantId = ticket.TenantId,
                ticketId = ticket.Id.Value,
                ticketNumber = SupportTicketNumber.Format(ticket.Number),
                assigneeMemberId = assignee,
                occurredAt,
            });
    }

    public void PublishUnassignedAlert(
        SupportTicket ticket,
        IReadOnlyList<Guid> recipientMemberIds,
        int thresholdHours,
        DateTimeOffset unassignedSince,
        DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(recipientMemberIds);
        Add(
            UnassignedAlertEvent,
            ticket,
            occurredAt,
            new
            {
                tenantId = ticket.TenantId,
                ticketId = ticket.Id.Value,
                ticketNumber = SupportTicketNumber.Format(ticket.Number),
                recipientMemberIds,
                thresholdHours,
                unassignedSince,
                occurredAt,
            });
    }

    public void PublishTaskAssigned(SupportTicket ticket, Guid assignedByMemberId, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        var assignee = ticket.AssigneeMemberId
            ?? throw new ArgumentException("A task-assigned event needs an assignee.", nameof(ticket));

        // Internal-tasks spec 2026-11-22 §6 D-3: exactly these five camelCase names are the contract.
        Add(
            TaskAssignedEvent,
            ticket,
            occurredAt,
            new
            {
                ticketId = ticket.Id.Value,
                tenantId = ticket.TenantId,
                ticketNumber = SupportTicketNumber.Format(ticket.Number),
                assigneeMemberId = assignee,
                assignedByMemberId,
            });
    }

    private void Add(string eventName, SupportTicket ticket, DateTimeOffset occurredAt, object payload) =>
        dbContext.Outbox.Add(new SupportOutboxMessage
        {
            Id = Guid.CreateVersion7(),
            EventName = eventName,
            PayloadJson = JsonSerializer.Serialize(payload),
            CorrelationId = ticket.Id.Value.ToString("D"),
            OccurredAt = occurredAt,
            Attempts = 0,
        });
}
