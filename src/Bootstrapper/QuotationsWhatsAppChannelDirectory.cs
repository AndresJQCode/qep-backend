using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Messaging.Application;
using Modules.Quotations.Application;

namespace Bootstrapper;

/// <summary>
/// Plan 2026-10-21 (Zenvia por tenant): el único punto donde Quotations e Integrations se tocan, como
/// <see cref="MessagingConnectionDirectory"/> para Messaging. Integrations ya filtra estado, visibilidad y habilitación
/// del proveedor; acá se descarta además la conexión que no tiene con qué mandar el documento pedido (spec 2026-11-09 D6):
/// para la cotización, sin token, número o plantilla de cotización (como una de Zenvia creada antes de que la plantilla fuera
/// campo de la conexión); para el pedido, Meta sin plantilla <c>Order</c> aprobada o Zenvia sin <c>orderTemplateId</c>; para la factura,
/// Meta sin plantilla <c>Invoice</c> aprobada o Zenvia sin <c>invoiceTemplateId</c>.
/// </summary>
internal sealed class QuotationsWhatsAppChannelDirectory(
    IIntegrationConnections connections, IQuotationTemplateReadiness templateReadiness, IOrderTemplateReadiness orderTemplateReadiness,
    IInvoiceTemplateReadiness invoiceTemplateReadiness)
    : IWhatsAppChannelDirectory
{
    public async Task<IReadOnlyList<WhatsAppChannel>> ListAvailableAsync(Guid tenantId, WhatsAppDocument document, CancellationToken cancellationToken)
    {
        var channels = new List<WhatsAppChannel>();

        // Spec 2026-10-21 §4.1: Meta primero. Una conexión sin la plantilla APPROVED del documento no puede mandarlo.
        // Cada proveedor llega resuelto en una sola lectura; la que no se resuelve (un secreto que no descifra) ya no viene.
        foreach (var resolved in await connections.ListResolvedActiveAsync(tenantId, IntegrationProviders.WhatsAppCloud.Key, cancellationToken))
        {
            if (!await HasApprovedTemplateAsync(tenantId, resolved.Id, document, cancellationToken))
            {
                continue;
            }

            // La plantilla no viaja: el sender lee la fila vigente al enviar.
            channels.Add(new MetaWhatsAppChannel(
                resolved.Id, resolved.Name, Value(resolved.Fields, WhatsAppCloudFieldKeys.DisplayPhoneNumber)));
        }

        foreach (var resolved in await connections.ListResolvedActiveAsync(tenantId, IntegrationProviders.Zenvia.Key, cancellationToken))
        {
            if (Value(resolved.Secrets, ZenviaFieldKeys.ApiToken) is not { } token
                || Value(resolved.Fields, ZenviaFieldKeys.FromNumber) is not { } fromNumber
                || Value(resolved.Fields, ZenviaFieldKeys.TemplateId) is not { } templateId)
            {
                continue;
            }

            // D6: para pedidos, una Zenvia sin orderTemplateId simplemente no está disponible (ya no es not_configured).
            var orderTemplateId = Value(resolved.Fields, ZenviaFieldKeys.OrderTemplateId);
            if (document == WhatsAppDocument.Order && orderTemplateId is null)
            {
                continue;
            }

            // Igual para la factura: sin invoiceTemplateId la Zenvia no está disponible.
            var invoiceTemplateId = Value(resolved.Fields, ZenviaFieldKeys.InvoiceTemplateId);
            if (document == WhatsAppDocument.Invoice && invoiceTemplateId is null)
            {
                continue;
            }

            channels.Add(new ZenviaWhatsAppChannel(resolved.Id, resolved.Name, token, fromNumber, templateId, orderTemplateId, invoiceTemplateId));
        }

        return channels;
    }

    private async Task<bool> HasApprovedTemplateAsync(Guid tenantId, Guid connectionId, WhatsAppDocument document, CancellationToken cancellationToken) =>
        document switch
        {
            WhatsAppDocument.Quotation => await templateReadiness.FindApprovedAsync(tenantId, connectionId, cancellationToken) is not null,
            WhatsAppDocument.Order => await orderTemplateReadiness.FindApprovedAsync(tenantId, connectionId, cancellationToken) is not null,
            WhatsAppDocument.Invoice => await invoiceTemplateReadiness.FindApprovedAsync(tenantId, connectionId, cancellationToken) is not null,
            _ => throw new ArgumentOutOfRangeException(nameof(document), document, "Unknown WhatsApp document."),
        };

    private static string? Value(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}
