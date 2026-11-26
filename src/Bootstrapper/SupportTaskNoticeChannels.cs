using Microsoft.Extensions.Options;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Notifications.Infrastructure;
using Modules.Support.Application;

namespace Bootstrapper;

/// <summary>
/// Task-notices spec 2026-11-22 §5.4: the WhatsApp connections Support offers for the task notices, as
/// <see cref="WholesaleWhatsAppConnections"/> does for Wholesale, except that the ones without an approved template stay in the list with
/// their status so the settings screen can say what is missing. Integrations filters state, visibility and provider enablement; Messaging
/// answers the raw Meta status of the <c>TaskNotice</c> row. The label is the visible number, null when there is none.
/// </summary>
internal sealed class SupportTaskNoticeChannels(
    IIntegrationConnections connections,
    ITaskNoticeTemplateReadiness readiness,
    IOptions<NotificationsOptions> options) : ISupportTaskNoticeChannels
{
    public int EscalationAfterMinutes => options.Value.TaskNotice.EscalationAfterMinutes;

    public async Task<IReadOnlyList<SupportTaskNoticeConnection>> ListAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var listed = new List<SupportTaskNoticeConnection>();
        foreach (var resolved in await connections.ListResolvedActiveAsync(
                     tenantId, IntegrationProviders.WhatsAppCloud.Key, cancellationToken))
        {
            var phone = resolved.Fields.TryGetValue(WhatsAppCloudFieldKeys.DisplayPhoneNumber, out var number)
                && !string.IsNullOrWhiteSpace(number)
                ? number
                : null;
            var status = await readiness.FindStatusAsync(tenantId, resolved.Id, cancellationToken);
            listed.Add(new SupportTaskNoticeConnection(resolved.Id, resolved.Name, phone, TemplateStatusOf(status)));
        }

        // One invariant order, so it does not depend on the server culture.
        return listed
            .OrderBy(connection => connection.Name, StringComparer.InvariantCultureIgnoreCase)
            .ThenBy(connection => connection.ConnectionId)
            .ToArray();
    }

    // Meta statuses travel raw and can be new ones: approved, in review (live) or anything else, which needs a fix.
    private static string TemplateStatusOf(string? metaStatus) =>
        metaStatus switch
        {
            null => SupportTaskNoticeTemplateStatuses.Missing,
            MessageTemplateStatuses.Approved => SupportTaskNoticeTemplateStatuses.Approved,
            _ when MessageTemplateStatuses.IsLive(metaStatus) => SupportTaskNoticeTemplateStatuses.Pending,
            _ => SupportTaskNoticeTemplateStatuses.Rejected,
        };
}
