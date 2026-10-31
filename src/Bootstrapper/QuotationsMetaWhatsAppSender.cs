using Modules.Messaging.Application;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Storage.Application;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-10-21 §4.1 y D16: el adaptador de <see cref="IMetaWhatsAppSender"/> hacia Mensajería. Lee el PDF privado de
/// Storage y le entrega a <see cref="IQuotationTemplateOutbound"/> la plantilla vigente de la conexión con los valores ya
/// formateados (los mismos que usa Zenvia); el mensaje queda en el hilo de la conversación. Sólo aquí se tocan Quotations,
/// Mensajería y Storage: Quotations no referencia ninguno de los dos.
/// </summary>
internal sealed class QuotationsMetaWhatsAppSender(
    IQuotationTemplateOutbound outbound, IObjectStorage storage, IQuotationTemplateReadiness readiness) : IMetaWhatsAppSender
{
    public async Task SendQuotationAsync(
        MetaWhatsAppChannel channel, string waId, WhatsAppQuotationMessage message, CancellationToken cancellationToken)
    {
        // SendQuotation siempre los llena: que falten es un error de programación, no del usuario.
        var tenantId = message.TenantId ?? throw new InvalidOperationException("The quotation message carries no tenant.");
        var sentBy = message.SentByMemberId ?? throw new InvalidOperationException("The quotation message carries no sender.");
        var storageKey = message.DocumentStorageKey ?? throw new InvalidOperationException("The quotation message carries no PDF.");

        // La vigente puede ser `_v2` o una que editó el tenant: la fila se lee al enviar, no se arrastra desde el directorio de canales. Si ya
        // no está aprobada (la pausaron o la borraron entre la pantalla y el envío), no hay nada que mandar.
        if (await readiness.FindApprovedAsync(tenantId, channel.ConnectionId, cancellationToken) is not { } template)
        {
            throw new QuotationsDomainException(
                "quotation.whatsapp.send_failed", "Meta did not accept the quotation (template_not_approved).");
        }

        var document = await storage.DownloadAsync(storageKey, cancellationToken);

        var result = await outbound.SendAsync(
            new QuotationTemplateSend(
                tenantId, channel.ConnectionId, sentBy,
                waId,
                message.CustomerId,
                message.FullName,
                template,
                message.FullName,
                message.OrderNumber,
                WhatsAppTemplateValues.Total(message.Total, message.Currency, message.NumberFormat),
                WhatsAppTemplateValues.ValidUntil(message.ValidUntil),
                document,
                WhatsAppTemplateValues.DocumentFileName(message.OrderNumber)),
            cancellationToken);

        if (result is not QuotationTemplateSendResult.Sent)
        {
            var reason = result switch
            {
                QuotationTemplateSendResult.ConnectionUnavailable unavailable => unavailable.Reason ?? "connection_unavailable",
                QuotationTemplateSendResult.Failed failed => failed.Code,
                _ => "unknown",
            };
            throw new QuotationsDomainException(
                "quotation.whatsapp.send_failed", $"Meta did not accept the quotation ({reason}).");
        }
    }
}
