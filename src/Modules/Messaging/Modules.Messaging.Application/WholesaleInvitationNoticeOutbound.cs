using BuildingBlocks.Application;

namespace Modules.Messaging.Application;

/// <summary>
/// Mayorista: el aviso por WhatsApp de una invitación de distribuidor. <c>WaId</c> son sólo dígitos, con indicativo. <c>SentByMemberId</c> es
/// quien invita o reenvía (la fila exige un autor). <c>ClientId</c> es el id del mensaje del outbox de Mayorista: un reintento después de un
/// envío que sí salió no manda un segundo aviso. <c>Template</c> es la fila aprobada que devuelve <see cref="IWholesaleInvitationNoticeReadiness"/>,
/// nunca la constante. <c>Values</c> va por clave (<c>nombre_contacto</c>, <c>mayorista</c>, <c>correo</c>); ni el link ni el token viajan.
/// </summary>
public sealed record WholesaleInvitationNoticeSend(
    Guid TenantId, Guid ConnectionId, Guid SentByMemberId, Guid ClientId,
    string WaId,
    string RecipientName,
    ApprovedTemplate Template,
    IReadOnlyDictionary<string, string> Values);

public abstract record WholesaleInvitationNoticeSendResult
{
    /// <summary>Salió, o ya había salido con ese <c>ClientId</c> (la misma fila).</summary>
    public sealed record Sent(Guid MessageId, Guid ConversationId) : WholesaleInvitationNoticeSendResult;

    /// <summary><paramref name="Reason"/> es un código de spec 2026-10-24 §3.4 (<c>paused</c>, <c>credentials_rejected</c>, …).</summary>
    public sealed record ConnectionUnavailable(string? Reason) : WholesaleInvitationNoticeSendResult;

    public sealed record Failed(string Code) : WholesaleInvitationNoticeSendResult;
}

public interface IWholesaleInvitationNoticeOutbound
{
    Task<WholesaleInvitationNoticeSendResult> SendAsync(WholesaleInvitationNoticeSend send, CancellationToken cancellationToken);
}

/// <summary>Mayorista: el aviso sale por <see cref="PlainTemplateOutbound"/>, sin cliente (el distribuidor invitado no es un cliente del tenant)
/// y sin orden posicional (la plantilla nació con variables con nombre).</summary>
public sealed class WholesaleInvitationNoticeOutbound(
    IConversationQueries conversations,
    IConversationRepository repository,
    IOutboundMessages outbound,
    IWhatsAppCloudClient meta,
    IMessagingConnectionDirectory connections,
    IMessagingNotifier notifier,
    ReadReceipts receipts,
    IClock clock,
    IConversationOpener opener,
    TemplateSenderValues senderValues) : IWholesaleInvitationNoticeOutbound
{
    private readonly PlainTemplateOutbound _core =
        new(conversations, repository, outbound, meta, connections, notifier, receipts, clock, opener, senderValues);

    public async Task<WholesaleInvitationNoticeSendResult> SendAsync(WholesaleInvitationNoticeSend send, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(send);
        var result = await _core.SendAsync(
            new PlainTemplateSend(
                send.TenantId, send.ConnectionId, send.SentByMemberId, send.ClientId, send.WaId, CustomerId: null, send.RecipientName,
                send.Template, send.Values, LegacyPositionalKeys: null),
            cancellationToken);
        return result switch
        {
            PlainTemplateSendResult.Sent sent => new WholesaleInvitationNoticeSendResult.Sent(sent.MessageId, sent.ConversationId),
            PlainTemplateSendResult.ConnectionUnavailable unavailable => new WholesaleInvitationNoticeSendResult.ConnectionUnavailable(unavailable.Reason),
            PlainTemplateSendResult.Failed failed => new WholesaleInvitationNoticeSendResult.Failed(failed.Code),
            _ => throw new InvalidOperationException($"Unexpected send result {result.GetType().Name}."),
        };
    }
}
