using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure.Persistence;

namespace Modules.Notifications.Infrastructure.InApp;

/// <summary>
/// Spec 2026-11-23 §9.3: the two bell entries of the occupational medical exams of Hr, for whoever holds <c>hr.medical.manage</c> (the
/// producer already chose the recipient and writes one message per member):
/// <list type="bullet">
/// <item><c>hr.medical-exams.due-digest.v1</c> becomes <see cref="InAppNotificationKind.HrMedicalExamsDue"/>, one entry per member and
/// day. The producer may publish it twice for the same day (a claim retaken after its lease expired), so the key is the day and not the
/// message: the second one finds the entry already written and changes nothing.</item>
/// <item><c>hr.medical-exams.exit-required.v1</c> becomes <see cref="InAppNotificationKind.HrExitExamRequired"/>, one per employee and
/// deadline, with the employee as the resource.</item>
/// </list>
/// The payloads carry no clinical text and no exam type, and the entries only keep what the screen draws.
/// </summary>
internal sealed class HrInAppConsumer(IServiceScopeFactory scopeFactory, ILogger<HrInAppConsumer> logger)
    : InAppOutboxConsumer(scopeFactory, logger)
{
    internal const string DueDigestEvent = "hr.medical-exams.due-digest.v1";

    internal const string ExitRequiredEvent = "hr.medical-exams.exit-required.v1";

    protected override string Consumer => "notifications.in-app.hr";

    protected override IReadOnlyCollection<string> EventNames => [DueDigestEvent, ExitRequiredEvent];

    protected override Task<IReadOnlyList<InAppNotification>> BuildAsync(
        OutboxRecord record, InAppContext context, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(record.PayloadJson);
        var root = document.RootElement;
        var tenantId = root.GetProperty("tenantId").GetGuid();
        var memberId = root.GetProperty("memberId").GetGuid();

        InAppNotification entry;
        if (record.EventName == DueDigestEvent)
        {
            var runDate = root.GetProperty("runDate").GetString() ?? string.Empty;
            entry = InAppNotification.Create(
                tenantId,
                memberId,
                InAppNotificationKind.HrMedicalExamsDue,
                resourceId: null,
                JsonSerializer.Serialize(new { pendingCount = root.GetProperty("pendingCount").GetInt32(), runDate }),
                dedupeKey: "hr-medical-exams:" + runDate,
                sourceMessageId: record.Id,
                createdAt: context.Clock.UtcNow);
        }
        else
        {
            var employeeId = root.GetProperty("employeeId").GetGuid();
            var dueDate = root.GetProperty("dueDate").GetString() ?? string.Empty;
            entry = InAppNotification.Create(
                tenantId,
                memberId,
                InAppNotificationKind.HrExitExamRequired,
                resourceId: employeeId,
                JsonSerializer.Serialize(new { employeeId, employeeName = root.GetProperty("employeeName").GetString(), dueDate }),
                dedupeKey: $"hr-exit-exam:{employeeId}:{dueDate}",
                sourceMessageId: record.Id,
                createdAt: context.Clock.UtcNow);
        }

        IReadOnlyList<InAppNotification> entries = [entry];
        return Task.FromResult(entries);
    }
}
