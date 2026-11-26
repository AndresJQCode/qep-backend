using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure.Persistence;

namespace Modules.Notifications.Infrastructure.InApp;

/// <summary>
/// Spec 2026-11-22 §6 and §12: the two Support events for the bell. <c>support.ticket.customer-replied.v1</c> tells the ticket's assignee
/// that the customer answered a ticket that was waiting (Support decides when, and never for the member who caused the change);
/// <c>support.ticket.unassigned-alert.v1</c> tells every recipient the tenant chose that a ticket has gone unassigned for too long,
/// one entry each. The second one may be published again for the same stretch (a retry of the worker), so its key is the ticket and the
/// moment it became unassigned, not the message; the first one, which is a single fact, is keyed by the message.
/// <para>Task-notices spec 2026-11-22: the two assignment events (<c>task-assigned</c> for an internal task, <c>assigned</c> for a customer
/// ticket, D-7) open an entry for the assignee plus one escalation row in the same transaction; <c>assignee-released</c> closes only the
/// former assignee's entry (D-2), never the one a reassignment just opened for the new assignee.</para>
/// </summary>
internal sealed class SupportInAppConsumer(
    IServiceScopeFactory scopeFactory, ILogger<SupportInAppConsumer> logger, IOptions<NotificationsOptions> options)
    : InAppOutboxConsumer(scopeFactory, logger)
{
    private const string TaskAssigned = "support.ticket.task-assigned.v1";
    private const string TicketAssigned = "support.ticket.assigned.v1";
    private const string AssigneeReleased = "support.ticket.assignee-released.v1";

    private const string CustomerReplied = "support.ticket.customer-replied.v1";
    private const string UnassignedAlert = "support.ticket.unassigned-alert.v1";

    protected override string Consumer => "notifications.in-app.support";

    protected override IReadOnlyCollection<string> EventNames => [CustomerReplied, UnassignedAlert, TaskAssigned, TicketAssigned, AssigneeReleased];

    protected override Task<IReadOnlyList<InAppNotification>> BuildAsync(
        OutboxRecord record, InAppContext context, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(record.PayloadJson);
        var root = document.RootElement;
        var tenantId = root.GetProperty("tenantId").GetGuid();
        var ticketId = root.GetProperty("ticketId").GetGuid();
        var ticketNumber = root.GetProperty("ticketNumber").GetString() ?? string.Empty;
        var createdAt = context.Clock.UtcNow;

        if (record.EventName is TaskAssigned or TicketAssigned)
        {
            return Task.FromResult(AssignedEntries(record, root, tenantId, ticketId, ticketNumber, createdAt));
        }

        if (string.Equals(record.EventName, AssigneeReleased, StringComparison.Ordinal))
        {
            // Closing is a resolution, not an entry.
            return Task.FromResult<IReadOnlyList<InAppNotification>>([]);
        }

        if (!string.Equals(record.EventName, CustomerReplied, StringComparison.Ordinal))
        {
            return Task.FromResult<IReadOnlyList<InAppNotification>>(
                UnassignedEntries(record, root, tenantId, ticketId, ticketNumber, createdAt));
        }

        IReadOnlyList<InAppNotification> replied =
        [
            InAppNotification.Create(
                tenantId,
                root.GetProperty("assigneeMemberId").GetGuid(),
                InAppNotificationKind.SupportCustomerReplied,
                resourceId: ticketId,
                JsonSerializer.Serialize(new { ticketId, ticketNumber }),
                dedupeKey: record.Id.ToString("D"),
                sourceMessageId: record.Id,
                createdAt),
        ];
        return Task.FromResult(replied);
    }

    protected override Task<IReadOnlyList<InAppResolution>> ResolutionsAsync(
        OutboxRecord record, InAppContext context, CancellationToken cancellationToken)
    {
        if (!string.Equals(record.EventName, AssigneeReleased, StringComparison.Ordinal))
        {
            return base.ResolutionsAsync(record, context, cancellationToken);
        }

        using var document = JsonDocument.Parse(record.PayloadJson);
        var root = document.RootElement;
        IReadOnlyList<InAppResolution> resolutions =
        [
            new InAppResolution(
                root.GetProperty("tenantId").GetGuid(),
                root.GetProperty("isInternal").GetBoolean()
                    ? InAppNotificationKind.SupportTaskAssigned
                    : InAppNotificationKind.SupportTicketAssigned,
                root.GetProperty("ticketId").GetGuid(),
                root.GetProperty("reason").GetString() ?? string.Empty,
                MemberId: root.GetProperty("formerAssigneeMemberId").GetGuid()),
        ];
        return Task.FromResult(resolutions);
    }

    protected override async Task OnInsertedAsync(
        NotificationsDbContext dbContext,
        OutboxRecord record,
        IReadOnlyList<InAppNotification> inserted,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (record.EventName is not (TaskAssigned or TicketAssigned))
        {
            return;
        }

        using var document = JsonDocument.Parse(record.PayloadJson);
        var assignedBy = document.RootElement.GetProperty("assignedByMemberId").GetGuid();
        var dueAt = (now + TimeSpan.FromMinutes(options.Value.TaskNotice.EscalationAfterMinutes)).ToUniversalTime();
        var created = now.ToUniversalTime();
        foreach (var entry in inserted)
        {
            // ON CONFLICT: the unique index on the entry makes this idempotent even if the entry came from an earlier claim.
            await dbContext.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO notifications.task_notice_escalations
                    (id, tenant_id, in_app_notification_id, ticket_id, member_id, assigned_by_member_id, due_at, status, created_at)
                VALUES ({Guid.CreateVersion7()}, {entry.TenantId}, {entry.Id}, {entry.ResourceId}, {entry.MemberId}, {assignedBy},
                        {dueAt}, 'Pending', {created})
                ON CONFLICT (in_app_notification_id) DO NOTHING
                """,
                CancellationToken.None);
        }
    }

    // The two assignment events share one payload and one rule; only the kind and the key prefix differ. A self-assignment tells nobody.
    private static IReadOnlyList<InAppNotification> AssignedEntries(
        OutboxRecord record, JsonElement root, Guid tenantId, Guid ticketId, string ticketNumber, DateTimeOffset createdAt)
    {
        var assignee = root.GetProperty("assigneeMemberId").GetGuid();
        var assignedBy = root.GetProperty("assignedByMemberId").GetGuid();
        if (assignee == assignedBy)
        {
            return [];
        }

        var isTask = string.Equals(record.EventName, TaskAssigned, StringComparison.Ordinal);
        var prefix = isTask ? "support-task-assigned" : "support-ticket-assigned";
        return
        [
            InAppNotification.Create(
                tenantId,
                assignee,
                isTask ? InAppNotificationKind.SupportTaskAssigned : InAppNotificationKind.SupportTicketAssigned,
                resourceId: ticketId,
                JsonSerializer.Serialize(new { ticketId, ticketNumber, actorMemberId = assignedBy, resolution = (string?)null }),
                dedupeKey: $"{prefix}:{ticketId:D}:{record.Id:D}",
                sourceMessageId: record.Id,
                createdAt),
        ];
    }

    private static List<InAppNotification> UnassignedEntries(
        OutboxRecord record, JsonElement root, Guid tenantId, Guid ticketId, string ticketNumber, DateTimeOffset createdAt)
    {
        var thresholdHours = root.GetProperty("thresholdHours").GetInt32();
        // The stretch, in UTC and the round-trip format: the same text on every retry, whatever offset the payload was written with.
        var unassignedSince = root.GetProperty("unassignedSince").GetDateTimeOffset().ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        var data = JsonSerializer.Serialize(new { ticketId, ticketNumber, thresholdHours });
        var dedupeKey = $"support-unassigned:{ticketId:D}:{unassignedSince}";

        return root.GetProperty("recipientMemberIds").EnumerateArray()
            .Select(recipient => recipient.GetGuid())
            .Distinct()
            .Select(memberId => InAppNotification.Create(
                tenantId,
                memberId,
                InAppNotificationKind.SupportTicketUnassigned,
                resourceId: ticketId,
                data,
                dedupeKey,
                sourceMessageId: record.Id,
                createdAt))
            .ToList();
    }
}
