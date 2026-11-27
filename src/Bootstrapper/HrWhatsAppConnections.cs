using Modules.Hr.Application.BirthdayNotices;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-11-26 §5.1: whether an id is a usable WhatsApp connection of the tenant, for the birthday notices setting. Integrations
/// already filters state, visibility and provider enablement in <c>ListResolvedActiveAsync</c> (as
/// <see cref="WholesaleWhatsAppConnections"/> relies on); a connection of another tenant, inactive or of another provider is simply
/// not in that list. Spec 2026-11-27: the same list with the status of the <c>HrBirthdayNotice</c> template on each connection, as
/// <see cref="SupportTaskNoticeChannels"/> does for the Support task notices. HR knows nothing else about WhatsApp.
/// </summary>
internal sealed class HrWhatsAppConnections(
    IIntegrationConnections connections, IHrBirthdayNoticeTemplateReadiness readiness) : IHrWhatsAppConnections
{
    public async Task<bool> IsWhatsAppConnectionAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        (await connections.ListResolvedActiveAsync(tenantId, IntegrationProviders.WhatsAppCloud.Key, cancellationToken))
            .Any(connection => connection.Id == connectionId);

    public async Task<IReadOnlyList<HrWhatsAppConnection>> ListAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var listed = new List<HrWhatsAppConnection>();
        foreach (var resolved in await connections.ListResolvedActiveAsync(
                     tenantId, IntegrationProviders.WhatsAppCloud.Key, cancellationToken))
        {
            var phone = resolved.Fields.TryGetValue(WhatsAppCloudFieldKeys.DisplayPhoneNumber, out var number)
                && !string.IsNullOrWhiteSpace(number)
                ? number
                : null;
            var status = await readiness.FindStatusAsync(tenantId, resolved.Id, cancellationToken);
            listed.Add(new HrWhatsAppConnection(resolved.Id, resolved.Name, phone, TemplateStatusOf(status)));
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
            null => HrBirthdayNoticeTemplateStatuses.Missing,
            MessageTemplateStatuses.Approved => HrBirthdayNoticeTemplateStatuses.Approved,
            _ when MessageTemplateStatuses.IsLive(metaStatus) => HrBirthdayNoticeTemplateStatuses.Pending,
            _ => HrBirthdayNoticeTemplateStatuses.Rejected,
        };
}
