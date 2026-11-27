using Modules.Messaging.Application;
using Modules.Notifications.Application;

namespace Bootstrapper;

/// <summary>
/// HR birthday notices, spec 2026-11-26 §5.3: the adapter of <see cref="IHrBirthdayWhatsApp"/> to Messaging, calqued on
/// <see cref="TaskNoticeWhatsAppSender"/>. It sends the approved <c>HrBirthdayNotice</c> template of the chosen connection (the name comes from
/// the row, not from a constant: the current one may be <c>_v2</c>) and fills exactly the four variables of that template: the contact name,
/// today's names, the date of the upcoming birthdays and their names. Messaging fills no fixed variable because the body has none besides the
/// contact name. Each value goes out in one line, as Meta requires. Results become the codes the delivery row keeps in <c>result_code</c>.
/// </summary>
internal sealed class HrBirthdayWhatsAppSender(IHrBirthdayNoticeTemplateReadiness readiness, IHrBirthdayNoticeOutbound outbound)
    : IHrBirthdayWhatsApp
{
    public async Task<TaskNoticeWhatsAppResult> SendAsync(HrBirthdayWhatsAppSend send, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(send);

        if (await readiness.FindApprovedAsync(send.TenantId, send.ConnectionId, cancellationToken) is not { } template)
        {
            return new TaskNoticeWhatsAppResult.Failed("template_not_approved");
        }

        var recipientName = TaskNoticeWhatsAppSender.OneLine(send.RecipientName);
        var result = await outbound.SendAsync(
            new HrBirthdayNoticeSend(
                send.TenantId,
                send.ConnectionId,
                send.SentByMemberId,
                send.ClientId,
                TaskNoticeWhatsAppSender.WaIdOf(send.PhoneE164),
                recipientName,
                template,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [SystemTemplateVariables.ContactName] = recipientName,
                    [HrBirthdayNoticeTemplateDefinition.TodayKey] = TaskNoticeWhatsAppSender.OneLine(send.Today),
                    [HrBirthdayNoticeTemplateDefinition.DateKey] = TaskNoticeWhatsAppSender.OneLine(send.UpcomingDate),
                    [HrBirthdayNoticeTemplateDefinition.UpcomingKey] = TaskNoticeWhatsAppSender.OneLine(send.Upcoming),
                }),
            cancellationToken);

        return result switch
        {
            TaskNoticeSendResult.Sent => new TaskNoticeWhatsAppResult.Sent(),
            TaskNoticeSendResult.ConnectionUnavailable unavailable =>
                new TaskNoticeWhatsAppResult.Failed($"connection_unavailable:{unavailable.Reason ?? "unknown"}"),
            TaskNoticeSendResult.Failed failed => new TaskNoticeWhatsAppResult.Failed($"send_failed:{failed.Code}"),
            _ => new TaskNoticeWhatsAppResult.Failed("send_failed:unknown"),
        };
    }
}
