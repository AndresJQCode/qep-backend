using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure.Persistence;

namespace Modules.Notifications.Infrastructure.InApp;

/// <summary>
/// Spec 2026-11-22 §6 and D-13: a wholesale order created for the advisor to approve opens an entry in that advisor's bell, and its approval
/// or cancellation closes it (resolved and read). Only the order's advisor is told, not everyone who can manage orders, so one person's
/// approval does not leave stale entries in the others' bells. The three events of Quotations are processed by this one consumer, in the
/// order of <c>occurred_at</c>, so an approval does not overtake its creation in the normal case.
/// <para>A cancellation is also published for an order that was already approved (<c>CancelOrder</c>), and that must not overwrite
/// «Approved»: only an entry still unresolved is closed. Residual: if the creation fails and is retried after the approval, the entry is
/// born unresolved.</para>
/// </summary>
internal sealed class WholesaleInAppConsumer(IServiceScopeFactory scopeFactory, ILogger<WholesaleInAppConsumer> logger)
    : InAppOutboxConsumer(scopeFactory, logger)
{
    private const string Created = "quotations.order.created.v1";
    private const string Approved = "quotations.order.wholesale-approved.v1";
    private const string Cancelled = "quotations.order.wholesale-cancelled.v1";

    // The value Quotations writes in the payload for an order that came from a distributor (OrderSource.Wholesale, by name).
    private const string WholesaleSource = "Wholesale";

    protected override string Consumer => "notifications.in-app.wholesale";

    protected override IReadOnlyCollection<string> EventNames => [Created, Approved, Cancelled];

    protected override Task<IReadOnlyList<InAppNotification>> BuildAsync(
        OutboxRecord record, InAppContext context, CancellationToken cancellationToken)
    {
        IReadOnlyList<InAppNotification> none = [];
        if (!string.Equals(record.EventName, Created, StringComparison.Ordinal))
        {
            return Task.FromResult(none);
        }

        using var document = JsonDocument.Parse(record.PayloadJson);
        var root = document.RootElement;
        // A manual or Shopify order has nothing to approve.
        if (!string.Equals(root.GetProperty("source").GetString(), WholesaleSource, StringComparison.Ordinal))
        {
            return Task.FromResult(none);
        }

        var orderId = root.GetProperty("orderId").GetGuid();
        var data = JsonSerializer.Serialize(new
        {
            orderId,
            orderNumber = root.GetProperty("orderNumber").GetString() ?? string.Empty,
            resolution = (string?)null,
        });
        IReadOnlyList<InAppNotification> entries =
        [
            InAppNotification.Create(
                root.GetProperty("tenantId").GetGuid(),
                root.GetProperty("advisorMemberId").GetGuid(),
                InAppNotificationKind.WholesaleOrderPendingApproval,
                resourceId: orderId,
                data,
                dedupeKey: record.Id.ToString("D"),
                sourceMessageId: record.Id,
                createdAt: context.Clock.UtcNow),
        ];
        return Task.FromResult(entries);
    }

    protected override Task<IReadOnlyList<InAppResolution>> ResolutionsAsync(
        OutboxRecord record, InAppContext context, CancellationToken cancellationToken)
    {
        var resolution = record.EventName switch
        {
            Approved => "Approved",
            Cancelled => "Cancelled",
            _ => null,
        };
        if (resolution is null)
        {
            return base.ResolutionsAsync(record, context, cancellationToken);
        }

        using var document = JsonDocument.Parse(record.PayloadJson);
        var root = document.RootElement;
        IReadOnlyList<InAppResolution> resolutions =
        [
            new InAppResolution(
                root.GetProperty("tenantId").GetGuid(),
                InAppNotificationKind.WholesaleOrderPendingApproval,
                root.GetProperty("orderId").GetGuid(),
                resolution),
        ];
        return Task.FromResult(resolutions);
    }
}
