using BuildingBlocks.Application;

namespace Modules.Messaging.Application;

/// <summary>
/// HR birthday notices: the WhatsApp digest of the birthdays sent to a manager. <c>WaId</c> is digits only, with country code.
/// <c>SentByMemberId</c> is the author the row requires (the member who configured the notice: it comes from the event). <c>ClientId</c> is the id
/// of the delivery: a retry after a send that did go out does not send a second notice. <c>Template</c> is the approved row returned by
/// <see cref="IHrBirthdayNoticeTemplateReadiness"/>, never the constant. <c>Values</c> carries <c>nombre_contacto</c>, <c>hoy</c>, <c>fecha</c> and
/// <c>proximos</c>: names and a date, never a birth year or an age.
/// </summary>
public sealed record HrBirthdayNoticeSend(
    Guid TenantId, Guid ConnectionId, Guid SentByMemberId, Guid ClientId,
    string WaId,
    string RecipientName,
    ApprovedTemplate Template,
    IReadOnlyDictionary<string, string> Values);

public interface IHrBirthdayNoticeOutbound
{
    Task<TaskNoticeSendResult> SendAsync(HrBirthdayNoticeSend send, CancellationToken cancellationToken);
}

/// <summary>HR birthday notices: like <see cref="TaskNoticeOutbound"/>, the notice goes through <see cref="PlainTemplateOutbound"/> with no customer
/// (the manager is a member of the tenant) and no positional order. It reuses <see cref="TaskNoticeSendResult"/>. It opens or reuses a conversation
/// with the member's number, so it shows up in the inbox.</summary>
public sealed class HrBirthdayNoticeOutbound(
    IConversationQueries conversations,
    IConversationRepository repository,
    IOutboundMessages outbound,
    IWhatsAppCloudClient meta,
    IMessagingConnectionDirectory connections,
    IMessagingNotifier notifier,
    ReadReceipts receipts,
    IClock clock,
    IConversationOpener opener,
    TemplateSenderValues senderValues) : IHrBirthdayNoticeOutbound
{
    private readonly PlainTemplateOutbound _core =
        new(conversations, repository, outbound, meta, connections, notifier, receipts, clock, opener, senderValues);

    public async Task<TaskNoticeSendResult> SendAsync(HrBirthdayNoticeSend send, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(send);
        var result = await _core.SendAsync(
            new PlainTemplateSend(
                send.TenantId, send.ConnectionId, send.SentByMemberId, send.ClientId, send.WaId, CustomerId: null, send.RecipientName,
                send.Template, send.Values, LegacyPositionalKeys: null),
            cancellationToken);
        return result switch
        {
            PlainTemplateSendResult.Sent sent => new TaskNoticeSendResult.Sent(sent.MessageId, sent.ConversationId),
            PlainTemplateSendResult.ConnectionUnavailable unavailable => new TaskNoticeSendResult.ConnectionUnavailable(unavailable.Reason),
            PlainTemplateSendResult.Failed failed => new TaskNoticeSendResult.Failed(failed.Code),
            _ => throw new InvalidOperationException($"Unexpected send result {result.GetType().Name}."),
        };
    }
}
