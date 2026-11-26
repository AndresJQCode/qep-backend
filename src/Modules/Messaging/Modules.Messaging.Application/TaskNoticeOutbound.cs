using BuildingBlocks.Application;

namespace Modules.Messaging.Application;

/// <summary>
/// Support task notices: the WhatsApp notice that a ticket or internal task was assigned. <c>WaId</c> is digits only, with country code.
/// <c>SentByMemberId</c> is who assigned: the row requires an author, and Messaging fills the fixed <c>asesor</c> variable with that member's
/// name. <c>ClientId</c> is the id of the escalation: a retry after a send that did go out does not send a second notice. <c>Template</c> is the
/// approved row returned by <see cref="ITaskNoticeTemplateReadiness"/>, never the constant. <c>Values</c> carries <c>numero</c>; neither the
/// subject nor the description ever travels.
/// </summary>
public sealed record TaskNoticeSend(
    Guid TenantId, Guid ConnectionId, Guid SentByMemberId, Guid ClientId,
    string WaId,
    string RecipientName,
    ApprovedTemplate Template,
    IReadOnlyDictionary<string, string> Values);

public abstract record TaskNoticeSendResult
{
    /// <summary>It went out, or it had already gone out with that <c>ClientId</c> (the same row).</summary>
    public sealed record Sent(Guid MessageId, Guid ConversationId) : TaskNoticeSendResult;

    /// <summary><paramref name="Reason"/> is a code of spec 2026-10-24 §3.4 (<c>paused</c>, <c>credentials_rejected</c>, …).</summary>
    public sealed record ConnectionUnavailable(string? Reason) : TaskNoticeSendResult;

    public sealed record Failed(string Code) : TaskNoticeSendResult;
}

public interface ITaskNoticeOutbound
{
    Task<TaskNoticeSendResult> SendAsync(TaskNoticeSend send, CancellationToken cancellationToken);
}

/// <summary>Support task notices: the notice goes through <see cref="PlainTemplateOutbound"/>, with no customer (the assignee is a member of
/// the tenant, not one of its customers) and no positional order (the template was born with named variables). Like the wholesale invitation
/// notice, it opens or reuses a conversation with the member's number, so it shows up in the inbox.</summary>
public sealed class TaskNoticeOutbound(
    IConversationQueries conversations,
    IConversationRepository repository,
    IOutboundMessages outbound,
    IWhatsAppCloudClient meta,
    IMessagingConnectionDirectory connections,
    IMessagingNotifier notifier,
    ReadReceipts receipts,
    IClock clock,
    IConversationOpener opener,
    TemplateSenderValues senderValues) : ITaskNoticeOutbound
{
    private readonly PlainTemplateOutbound _core =
        new(conversations, repository, outbound, meta, connections, notifier, receipts, clock, opener, senderValues);

    public async Task<TaskNoticeSendResult> SendAsync(TaskNoticeSend send, CancellationToken cancellationToken)
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
