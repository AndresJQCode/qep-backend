using System.Text.RegularExpressions;
using Modules.Messaging.Application;
using Modules.Notifications.Application;

namespace Bootstrapper;

/// <summary>
/// Task-notices spec 2026-11-22 §5.6: the adapter of <see cref="ITaskNoticeWhatsApp"/> to Messaging, calqued on
/// <see cref="WholesaleInvitationNoticeSender"/>. It sends the approved <c>TaskNotice</c> template of the chosen connection (the name comes
/// from the row, not from a constant: the current one may be <c>_v2</c>) and fills only what is its own, the ticket number and the contact
/// name; Messaging fills the fixed variables, among them <c>asesor</c> with the name of <see cref="TaskNoticeWhatsAppSend.SentByMemberId"/>.
/// The subject and the description never travel to Meta. Results become the codes the escalation keeps in <c>channel_attempts</c>.
/// </summary>
internal sealed partial class TaskNoticeWhatsAppSender(ITaskNoticeTemplateReadiness readiness, ITaskNoticeOutbound outbound)
    : ITaskNoticeWhatsApp
{
    public async Task<TaskNoticeWhatsAppResult> SendAsync(TaskNoticeWhatsAppSend send, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(send);

        if (await readiness.FindApprovedAsync(send.TenantId, send.ConnectionId, cancellationToken) is not { } template)
        {
            return new TaskNoticeWhatsAppResult.Failed("template_not_approved");
        }

        var result = await outbound.SendAsync(
            new TaskNoticeSend(
                send.TenantId,
                send.ConnectionId,
                send.SentByMemberId,
                send.ClientId,
                WaIdOf(send.PhoneE164),
                OneLine(send.RecipientName),
                template,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [TaskNoticeTemplateDefinition.NumberKey] = send.TicketNumber,
                    [SystemTemplateVariables.ContactName] = OneLine(send.RecipientName),
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

    // Meta's wa_id is digits only, with the country code; the stored phone is E.164 («+573001234567»).
    private static string WaIdOf(string phoneE164) => new(phoneE164.Where(char.IsAsciiDigit).ToArray());

    // Meta rejects line breaks, tabs and more than four spaces in a row: one line, single spaces.
    private static string OneLine(string? value) => value is null ? string.Empty : Whitespace().Replace(value, " ").Trim();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
