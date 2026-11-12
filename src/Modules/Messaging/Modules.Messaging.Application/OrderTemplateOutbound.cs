using BuildingBlocks.Application;

namespace Modules.Messaging.Application;

/// <summary>
/// Spec 2026-11-09 D5: lo que Quotations manda por Meta para un pedido, hermano de <see cref="QuotationTemplateSend"/> sin vigencia.
/// <c>Template</c> es la fila <c>Order</c> aprobada que devuelve <see cref="IOrderTemplateReadiness"/>, nunca la constante. Los valores ya
/// vienen formateados; sólo viajan los que la fila usa.
/// </summary>
public sealed record OrderTemplateSend(
    Guid TenantId, Guid ConnectionId, Guid SentByMemberId,
    string WaId,
    Guid? CustomerId,
    string RecipientName,
    ApprovedTemplate Template,
    string FullName, string OrderNumber, string Total,
    ReadOnlyMemory<byte> Document, string DocumentFileName);

public abstract record OrderTemplateSendResult
{
    public sealed record Sent(Guid MessageId, Guid ConversationId) : OrderTemplateSendResult;

    public sealed record ConnectionUnavailable(string? Reason) : OrderTemplateSendResult;

    public sealed record Failed(string Code) : OrderTemplateSendResult;
}

public interface IOrderTemplateOutbound
{
    Task<OrderTemplateSendResult> SendAsync(OrderTemplateSend send, CancellationToken cancellationToken);
}

/// <summary>Spec 2026-11-09 D5: el mismo procedimiento que la cotización (<see cref="DocumentTemplateOutbound"/>) con las claves del pedido:
/// <c>nombre_contacto</c>, <c>numero_pedido</c> y <c>valor_total</c>; <c>asesor</c>, <c>empresa</c> y <c>saludo</c> los agrega
/// <see cref="TemplateSenderValues"/> si la fila los usa.</summary>
public sealed class OrderTemplateOutbound(
    IConversationQueries conversations,
    IConversationRepository repository,
    IOutboundMessages outbound,
    IWhatsAppCloudClient meta,
    IMessagingConnectionDirectory connections,
    IMessagingNotifier notifier,
    ReadReceipts receipts,
    IClock clock,
    IConversationOpener opener,
    IMessagingMediaStore store,
    TemplateSenderValues senderValues) : IOrderTemplateOutbound
{
    private const string OrderNumberKey = "numero_pedido";
    private const string TotalKey = "valor_total";

    private readonly DocumentTemplateOutbound core = new(conversations, repository, outbound, meta, connections, notifier, receipts, clock, opener, store, senderValues);

    public async Task<OrderTemplateSendResult> SendAsync(OrderTemplateSend send, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(send);
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SystemTemplateVariables.ContactName] = send.FullName,
            [OrderNumberKey] = send.OrderNumber,
            [TotalKey] = send.Total,
        };
        var result = await core.SendAsync(
            new DocumentTemplateSend(
                send.TenantId, send.ConnectionId, send.SentByMemberId, send.WaId, send.CustomerId, send.RecipientName, send.Template, values,
                send.Document, send.DocumentFileName),
            cancellationToken);
        return result switch
        {
            QuotationTemplateSendResult.Sent sent => new OrderTemplateSendResult.Sent(sent.MessageId, sent.ConversationId),
            QuotationTemplateSendResult.ConnectionUnavailable unavailable => new OrderTemplateSendResult.ConnectionUnavailable(unavailable.Reason),
            QuotationTemplateSendResult.Failed failed => new OrderTemplateSendResult.Failed(failed.Code),
            _ => throw new InvalidOperationException($"Unknown send result '{result.GetType().Name}'."),
        };
    }
}
