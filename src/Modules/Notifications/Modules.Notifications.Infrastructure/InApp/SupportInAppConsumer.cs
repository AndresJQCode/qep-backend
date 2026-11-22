using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure.Persistence;

namespace Modules.Notifications.Infrastructure.InApp;

/// <summary>
/// Spec 2026-11-22 §6 and §12: the two Support events for the bell. <c>support.ticket.customer-replied.v1</c> tells the ticket's assignee
/// that the customer answered a ticket that was waiting (Support decides when, and never for the member who caused the change);
/// <c>support.ticket.unassigned-alert.v1</c> tells every recipient the tenant chose that a ticket has gone unassigned for too long,
/// one entry each. The second one may be published again for the same stretch (a retry of the worker), so its key is the ticket and the
/// moment it became unassigned, not the message; the first one, which is a single fact, is keyed by the message.
/// </summary>
internal sealed class SupportInAppConsumer(IServiceScopeFactory scopeFactory, ILogger<SupportInAppConsumer> logger)
    : InAppOutboxConsumer(scopeFactory, logger)
{
    private const string CustomerReplied = "support.ticket.customer-replied.v1";
    private const string UnassignedAlert = "support.ticket.unassigned-alert.v1";

    protected override string Consumer => "notifications.in-app.support";

    protected override IReadOnlyCollection<string> EventNames => [CustomerReplied, UnassignedAlert];

    protected override Task<IReadOnlyList<InAppNotification>> BuildAsync(
        OutboxRecord record, InAppContext context, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(record.PayloadJson);
        var root = document.RootElement;
        var tenantId = root.GetProperty("tenantId").GetGuid();
        var ticketId = root.GetProperty("ticketId").GetGuid();
        var ticketNumber = root.GetProperty("ticketNumber").GetString() ?? string.Empty;
        var createdAt = context.Clock.UtcNow;

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
