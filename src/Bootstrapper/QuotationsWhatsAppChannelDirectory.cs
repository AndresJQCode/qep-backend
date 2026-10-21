using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Quotations.Application;

namespace Bootstrapper;

/// <summary>
/// Plan 2026-10-21 (Zenvia por tenant): el único punto donde Quotations e Integrations se tocan, como
/// <see cref="MessagingConnectionDirectory"/> para Messaging. Integrations ya filtra estado, visibilidad y habilitación
/// del proveedor; acá se descarta además la conexión que no tiene con qué mandar una cotización (sin token, número o
/// plantilla), como una de Zenvia creada antes de que la plantilla fuera campo de la conexión.
/// </summary>
internal sealed class QuotationsWhatsAppChannelDirectory(IIntegrationConnections connections) : IWhatsAppChannelDirectory
{
    public async Task<IReadOnlyList<WhatsAppChannel>> ListAvailableAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var channels = new List<WhatsAppChannel>();
        foreach (var summary in await connections.ListActiveAsync(tenantId, IntegrationProviders.Zenvia.Key, cancellationToken))
        {
            var resolved = await connections.ResolveAsync(tenantId, summary.Id, cancellationToken);
            if (resolved is null
                || Value(resolved.Secrets, ZenviaFieldKeys.ApiToken) is not { } token
                || Value(resolved.Fields, ZenviaFieldKeys.FromNumber) is not { } fromNumber
                || Value(resolved.Fields, ZenviaFieldKeys.TemplateId) is not { } templateId)
            {
                continue;
            }

            channels.Add(new WhatsAppChannel(
                resolved.Id, token, fromNumber, templateId, Value(resolved.Fields, ZenviaFieldKeys.OrderTemplateId)));
        }

        return channels;
    }

    private static string? Value(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}
