using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure.Persistence;

namespace Modules.Notifications.Infrastructure.InApp;

/// <summary>
/// Spec 2026-11-22 §6: an export that is ready, or (for Quotations and Orders) failed, reaches the bell of whoever asked for it. The
/// events carry the requesting <b>user</b> (<c>subjectId</c>); a bell entry belongs to a <b>membership</b> (D-5), so it is translated, and
/// without an active membership the message is processed with no entry. Products, customers, suppliers and warehouse stock only have
/// «ready»: their failures are synchronous HTTP errors and leave no event.
/// </summary>
internal sealed partial class ExportsInAppConsumer(IServiceScopeFactory scopeFactory, ILogger<ExportsInAppConsumer> logger)
    : InAppOutboxConsumer(scopeFactory, logger)
{
    private const string QuotationsReady = "quotations.export-ready.v1";
    private const string QuotationsFailed = "quotations.export-failed.v1";

    private static readonly IReadOnlyDictionary<string, (string ExportKind, string CountProperty)> FixedKinds =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["catalog.product-export-ready.v1"] = ("Products", "productCount"),
            ["customers.export-ready.v1"] = ("Customers", "customerCount"),
            ["suppliers.export-ready.v1"] = ("Suppliers", "supplierCount"),
            ["wms.export-ready.v1"] = ("Stock", "rowCount"),
        };

    private readonly ILogger<ExportsInAppConsumer> _logger = logger;

    protected override string Consumer => "notifications.in-app.exports";

    protected override IReadOnlyCollection<string> EventNames =>
        [.. FixedKinds.Keys, QuotationsReady, QuotationsFailed];

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Outbox message {MessageId} of {Consumer}: the requester has no active membership in the tenant, so no entry is created.")]
    private static partial void LogNoMembership(ILogger logger, Guid messageId, string consumer);

    protected override async Task<IReadOnlyList<InAppNotification>> BuildAsync(
        OutboxRecord record, InAppContext context, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(record.PayloadJson);
        var root = document.RootElement;
        var tenantId = root.GetProperty("tenantId").GetGuid();
        var subjectId = root.GetProperty("subjectId").GetGuid();

        var memberId = await context.Memberships.FindActiveMembershipIdAsync(subjectId, tenantId, cancellationToken);
        if (memberId is null)
        {
            LogNoMembership(_logger, record.Id, Consumer);
            return [];
        }

        var failed = string.Equals(record.EventName, QuotationsFailed, StringComparison.Ordinal);
        var (kind, data) = failed ? FailedData(root) : ReadyData(record.EventName, root);
        return
        [
            InAppNotification.Create(
                tenantId,
                memberId.Value,
                kind,
                resourceId: null,
                data,
                dedupeKey: record.Id.ToString("D"),
                sourceMessageId: record.Id,
                createdAt: context.Clock.UtcNow),
        ];
    }

    private static (InAppNotificationKind Kind, string Data) FailedData(JsonElement root) =>
        (InAppNotificationKind.ExportFailed, JsonSerializer.Serialize(new { exportKind = root.GetProperty("kind").GetString() ?? string.Empty }));

    // The link is stored because it cannot be derived again (D-12); the retention scrubs it once it has expired.
    private static (InAppNotificationKind Kind, string Data) ReadyData(string eventName, JsonElement root)
    {
        var (exportKind, countProperty) = FixedKinds.TryGetValue(eventName, out var fixedKind)
            ? fixedKind
            : (root.GetProperty("kind").GetString() ?? string.Empty, "rowCount");
        var data = JsonSerializer.Serialize(new
        {
            exportKind,
            fileName = root.GetProperty("fileName").GetString() ?? string.Empty,
            rowCount = root.GetProperty(countProperty).GetInt32(),
            expiresAt = root.GetProperty("expiresAt").GetDateTimeOffset().ToString("O"),
            downloadUrl = root.GetProperty("downloadUrl").GetString(),
        });
        return (InAppNotificationKind.ExportReady, data);
    }
}
