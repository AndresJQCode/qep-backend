using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure.Persistence;

namespace Modules.Notifications.Infrastructure.InApp;

/// <summary>
/// The bell of the service contracts, for each member who receives their daily digest: «tienes N cobros vencidos»
/// (<c>billing.charges.overdue-digest.v1</c>, spec 2026-11-22 §6 and D-17) and the price reviews that are due or holding a charge
/// (<c>billing.price-reviews.digest.v1</c>, spec reajuste §6.6). One entry of each per member and day. The producer publishes each event
/// only when it has something to tell, and may publish it twice for the same day (a claim retaken after its lease expired), so the key is
/// the day and not the message: the second one finds the entry already written and changes nothing.
/// </summary>
internal sealed class BillingInAppConsumer(IServiceScopeFactory scopeFactory, ILogger<BillingInAppConsumer> logger)
    : InAppOutboxConsumer(scopeFactory, logger)
{
    protected override string Consumer => "notifications.in-app.billing";

    private const string OverdueEvent = "billing.charges.overdue-digest.v1";

    private const string PriceReviewEvent = "billing.price-reviews.digest.v1";

    protected override IReadOnlyCollection<string> EventNames => [OverdueEvent, PriceReviewEvent];

    protected override Task<IReadOnlyList<InAppNotification>> BuildAsync(
        OutboxRecord record, InAppContext context, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(record.PayloadJson);
        var root = document.RootElement;
        var runDate = root.GetProperty("runDate").GetString() ?? string.Empty;
        var (kind, data, dedupePrefix) = record.EventName == PriceReviewEvent
            ? (
                InAppNotificationKind.ServicePriceReviewsDue,
                JsonSerializer.Serialize(new
                {
                    dueCount = root.GetProperty("dueCount").GetInt32(),
                    heldCount = root.GetProperty("heldCount").GetInt32(),
                    runDate,
                }),
                "billing-price-review:")
            : (
                InAppNotificationKind.ServiceChargesOverdue,
                JsonSerializer.Serialize(new { overdueCount = root.GetProperty("overdueCount").GetInt32(), runDate }),
                "billing-overdue:");
        IReadOnlyList<InAppNotification> entries =
        [
            InAppNotification.Create(
                root.GetProperty("tenantId").GetGuid(),
                root.GetProperty("memberId").GetGuid(),
                kind,
                resourceId: null,
                data,
                dedupeKey: dedupePrefix + runDate,
                sourceMessageId: record.Id,
                createdAt: context.Clock.UtcNow),
        ];
        return Task.FromResult(entries);
    }
}
