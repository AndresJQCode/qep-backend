using BuildingBlocks.Application;

namespace Modules.Messaging.Application;

/// <summary>
/// Spec 2026-11-21: lo que Quotations manda por Meta para la factura de Siigo, hermano de <see cref="OrderTemplateSend"/>. <c>Template</c>
/// es la fila <c>Invoice</c> aprobada que devuelve <see cref="IInvoiceTemplateReadiness"/>, nunca la constante. Los valores ya vienen
/// formateados; sólo viajan los que la fila usa. <c>InvoiceNumber</c> es el número de la factura, no el del pedido.
/// </summary>
public sealed record InvoiceTemplateSend(
    Guid TenantId, Guid ConnectionId, Guid SentByMemberId,
    string WaId,
    Guid? CustomerId,
    string RecipientName,
    ApprovedTemplate Template,
    string FullName, string InvoiceNumber, string Total,
    ReadOnlyMemory<byte> Document, string DocumentFileName);

public abstract record InvoiceTemplateSendResult
{
    public sealed record Sent(Guid MessageId, Guid ConversationId) : InvoiceTemplateSendResult;

    public sealed record ConnectionUnavailable(string? Reason) : InvoiceTemplateSendResult;

    public sealed record Failed(string Code) : InvoiceTemplateSendResult;
}

public interface IInvoiceTemplateOutbound
{
    Task<InvoiceTemplateSendResult> SendAsync(InvoiceTemplateSend send, CancellationToken cancellationToken);
}

/// <summary>Spec 2026-11-21: el mismo procedimiento que el pedido (<see cref="DocumentTemplateOutbound"/>) con las claves de la factura:
/// <c>nombre_contacto</c>, <c>numero_factura</c> y <c>valor_total</c>; <c>asesor</c>, <c>empresa</c> y <c>saludo</c> los agrega
/// <see cref="TemplateSenderValues"/> si la fila los usa.</summary>
public sealed class InvoiceTemplateOutbound(
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
    TemplateSenderValues senderValues) : IInvoiceTemplateOutbound
{
    private const string InvoiceNumberKey = "numero_factura";
    private const string TotalKey = "valor_total";

    private readonly DocumentTemplateOutbound core = new(conversations, repository, outbound, meta, connections, notifier, receipts, clock, opener, store, senderValues);

    public async Task<InvoiceTemplateSendResult> SendAsync(InvoiceTemplateSend send, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(send);
        return Map(await core.SendAsync(ToDocumentSend(send), cancellationToken));
    }

    /// <summary>Lo que recibe el núcleo: las tres claves propias de la factura y el resto del envío tal cual.</summary>
    internal static DocumentTemplateSend ToDocumentSend(InvoiceTemplateSend send)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SystemTemplateVariables.ContactName] = send.FullName,
            [InvoiceNumberKey] = send.InvoiceNumber,
            [TotalKey] = send.Total,
        };
        return new DocumentTemplateSend(
            send.TenantId, send.ConnectionId, send.SentByMemberId, send.WaId, send.CustomerId, send.RecipientName, send.Template, values,
            send.Document, send.DocumentFileName);
    }

    internal static InvoiceTemplateSendResult Map(QuotationTemplateSendResult result) => result switch
    {
        QuotationTemplateSendResult.Sent sent => new InvoiceTemplateSendResult.Sent(sent.MessageId, sent.ConversationId),
        QuotationTemplateSendResult.ConnectionUnavailable unavailable => new InvoiceTemplateSendResult.ConnectionUnavailable(unavailable.Reason),
        QuotationTemplateSendResult.Failed failed => new InvoiceTemplateSendResult.Failed(failed.Code),
        _ => throw new InvalidOperationException($"Unknown send result '{result.GetType().Name}'."),
    };
}
