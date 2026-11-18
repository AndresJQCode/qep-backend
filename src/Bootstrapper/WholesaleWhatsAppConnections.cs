using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Messaging.Application;
using Modules.Wholesale.Application;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-11-18: las conexiones con que Mayorista puede mandar el aviso de la invitación, como
/// <see cref="QuotationsWhatsAppChannelDirectory"/> para las cotizaciones. Integrations filtra estado, visibilidad y
/// habilitación del proveedor; acá se descarta la que no tiene la plantilla del aviso aprobada. El label es el número visible
/// y, sin número, el nombre de la conexión (D-7).
/// </summary>
internal sealed class WholesaleWhatsAppConnections(
    IIntegrationConnections connections, IWholesaleInvitationNoticeReadiness readiness) : IWholesaleWhatsAppConnections
{
    public async Task<IReadOnlyList<WholesaleWhatsAppConnection>> ListReadyAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var ready = new List<WholesaleWhatsAppConnection>();
        foreach (var resolved in await connections.ListResolvedActiveAsync(
                     tenantId, IntegrationProviders.WhatsAppCloud.Key, cancellationToken))
        {
            if (await readiness.FindApprovedAsync(tenantId, resolved.Id, cancellationToken) is not null)
            {
                ready.Add(new WholesaleWhatsAppConnection(resolved.Id, Label(resolved)));
            }
        }

        return ready;
    }

    // Lista o no para el aviso: el detalle nombra la conexión por la que salió aunque la plantilla ya no esté aprobada.
    public async Task<string?> FindLabelAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        (await connections.ResolveAsync(tenantId, connectionId, cancellationToken)).Connection is { } connection
            ? Label(connection)
            : null;

    private static string Label(ResolvedConnection connection) =>
        connection.Fields.TryGetValue(WhatsAppCloudFieldKeys.DisplayPhoneNumber, out var phone) && !string.IsNullOrWhiteSpace(phone)
            ? phone
            : connection.Name;
}
