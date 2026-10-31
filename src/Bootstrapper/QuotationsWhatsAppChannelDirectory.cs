using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Messaging.Application;
using Modules.Quotations.Application;

namespace Bootstrapper;

/// <summary>
/// Plan 2026-10-21 (Zenvia por tenant): el único punto donde Quotations e Integrations se tocan, como
/// <see cref="MessagingConnectionDirectory"/> para Messaging. Integrations ya filtra estado, visibilidad y habilitación
/// del proveedor; acá se descarta además la conexión que no tiene con qué mandar una cotización (sin token, número o
/// plantilla), como una de Zenvia creada antes de que la plantilla fuera campo de la conexión.
/// </summary>
internal sealed class QuotationsWhatsAppChannelDirectory(
    IIntegrationConnections connections, IQuotationTemplateReadiness templateReadiness) : IWhatsAppChannelDirectory
{
    public async Task<IReadOnlyList<WhatsAppChannel>> ListAvailableAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var channels = new List<WhatsAppChannel>();

        // Spec 2026-10-21 §4.1: Meta primero. Una conexión sin plantilla de cotización APPROVED no puede mandar nada.
        foreach (var summary in await connections.ListActiveAsync(tenantId, IntegrationProviders.WhatsAppCloud.Key, cancellationToken))
        {
            var resolved = (await connections.ResolveAsync(tenantId, summary.Id, cancellationToken)).Connection;
            if (resolved is null
                || await templateReadiness.FindApprovedAsync(tenantId, resolved.Id, cancellationToken) is null)
            {
                continue;
            }

            // La plantilla no viaja: el sender lee la fila vigente al enviar.
            channels.Add(new MetaWhatsAppChannel(
                resolved.Id, resolved.Name, Value(resolved.Fields, WhatsAppCloudFieldKeys.DisplayPhoneNumber)));
        }

        foreach (var summary in await connections.ListActiveAsync(tenantId, IntegrationProviders.Zenvia.Key, cancellationToken))
        {
            var resolved = (await connections.ResolveAsync(tenantId, summary.Id, cancellationToken)).Connection;
            if (resolved is null
                || Value(resolved.Secrets, ZenviaFieldKeys.ApiToken) is not { } token
                || Value(resolved.Fields, ZenviaFieldKeys.FromNumber) is not { } fromNumber
                || Value(resolved.Fields, ZenviaFieldKeys.TemplateId) is not { } templateId)
            {
                continue;
            }

            channels.Add(new ZenviaWhatsAppChannel(
                resolved.Id, resolved.Name, token, fromNumber, templateId, Value(resolved.Fields, ZenviaFieldKeys.OrderTemplateId)));
        }

        return channels;
    }

    private static string? Value(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}
