using BuildingBlocks.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>
/// Cobros recurrentes §5: lo que Billing manda por la conexión de WhatsApp Business elegida. <c>WaId</c> son sólo dígitos, con indicativo.
/// <c>SentByMemberId</c> es el encargado del perfil de cobro (la fila exige un autor). <c>CustomerId</c> liga la conversación nueva al
/// cliente. <c>Template</c> es la fila aprobada que devuelve <see cref="IBillingTemplateReadiness"/>, nunca la constante: tras un reintento la
/// vigente puede ser <c>qep_cobro_recordatorio_v2</c>, y su cuerpo puede ser el que editó el tenant. <c>Values</c> ya vienen formateados y por
/// clave (<c>nombre_contacto</c>, <c>servicio</c>…); sólo viajan los que la fila usa.
/// </summary>
public sealed record BillingTemplateSend(
    Guid TenantId, Guid ConnectionId, Guid SentByMemberId,
    string WaId,
    Guid? CustomerId,
    string RecipientName,
    MessageTemplateKind Kind,
    ApprovedTemplate Template,
    IReadOnlyDictionary<string, string> Values);

public abstract record BillingTemplateSendResult
{
    public sealed record Sent(Guid MessageId, Guid ConversationId) : BillingTemplateSendResult;

    /// <summary><paramref name="Reason"/> es un código de spec 2026-10-24 §3.4 (<c>paused</c>, <c>secret_unreadable</c>,
    /// <c>credentials_rejected</c>, …), como en el envío del bot.</summary>
    public sealed record ConnectionUnavailable(string? Reason) : BillingTemplateSendResult;

    public sealed record Failed(string Code) : BillingTemplateSendResult;
}

public interface IBillingTemplateOutbound
{
    Task<BillingTemplateSendResult> SendAsync(BillingTemplateSend send, CancellationToken cancellationToken);
}

/// <summary>
/// Cobros recurrentes §5: la plantilla de cobro al hilo del número en la conexión. El envío es el de <see cref="PlainTemplateOutbound"/>, con
/// un <c>clientId</c> nuevo (cada aviso es un mensaje propio, no un reintento) y el orden posicional de su tipo para las filas viejas.
/// </summary>
public sealed class BillingTemplateOutbound(
    IConversationQueries conversations,
    IConversationRepository repository,
    IOutboundMessages outbound,
    IWhatsAppCloudClient meta,
    IMessagingConnectionDirectory connections,
    IMessagingNotifier notifier,
    ReadReceipts receipts,
    IClock clock,
    IConversationOpener opener,
    TemplateSenderValues senderValues) : IBillingTemplateOutbound
{
    private readonly PlainTemplateOutbound _core =
        new(conversations, repository, outbound, meta, connections, notifier, receipts, clock, opener, senderValues);

    public async Task<BillingTemplateSendResult> SendAsync(BillingTemplateSend send, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(send);
        var result = await _core.SendAsync(
            new PlainTemplateSend(
                send.TenantId, send.ConnectionId, send.SentByMemberId, Guid.CreateVersion7(), send.WaId, send.CustomerId, send.RecipientName,
                send.Template, send.Values, BillingTemplateDefinitions.For(send.Kind).LegacyPositionalKeys),
            cancellationToken);
        return result switch
        {
            PlainTemplateSendResult.Sent sent => new BillingTemplateSendResult.Sent(sent.MessageId, sent.ConversationId),
            PlainTemplateSendResult.ConnectionUnavailable unavailable => new BillingTemplateSendResult.ConnectionUnavailable(unavailable.Reason),
            PlainTemplateSendResult.Failed failed => new BillingTemplateSendResult.Failed(failed.Code),
            _ => throw new InvalidOperationException($"Unexpected send result {result.GetType().Name}."),
        };
    }
}
