using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure.Persistence;

namespace Modules.Notifications.Infrastructure.InApp;

/// <summary>
/// Spec 2026-11-22 §6 and D-17: «tienes N cobros vencidos», one entry per member and day, for each member who receives the daily digest of
/// the service contracts. The producer publishes <c>billing.charges.overdue-digest.v1</c> only when there is something overdue, and may
/// publish it twice for the same day (a claim retaken after its lease expired), so the key is the day and not the message: the second one
/// finds the entry already written and changes nothing.
/// </summary>
internal sealed class BillingInAppConsumer(IServiceScopeFactory scopeFactory, ILogger<BillingInAppConsumer> logger)
    : InAppOutboxConsumer(scopeFactory, logger)
{
    protected override string Consumer => "notifications.in-app.billing";

    protected override IReadOnlyCollection<string> EventNames => ["billing.charges.overdue-digest.v1"];

    protected override Task<IReadOnlyList<InAppNotification>> BuildAsync(
        OutboxRecord record, InAppContext context, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(record.PayloadJson);
        var root = document.RootElement;
        var runDate = root.GetProperty("runDate").GetString() ?? string.Empty;
        var data = JsonSerializer.Serialize(new { overdueCount = root.GetProperty("overdueCount").GetInt32(), runDate });
        IReadOnlyList<InAppNotification> entries =
        [
            InAppNotification.Create(
                root.GetProperty("tenantId").GetGuid(),
                root.GetProperty("memberId").GetGuid(),
                InAppNotificationKind.ServiceChargesOverdue,
                resourceId: null,
                data,
                dedupeKey: "billing-overdue:" + runDate,
                sourceMessageId: record.Id,
                createdAt: context.Clock.UtcNow),
        ];
        return Task.FromResult(entries);
    }
}
