using Modules.Automations.Application;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-10-21 §4.4 y D-M6: el único punto donde Automations ve las conexiones de Mensajería. <c>ListAllAsync</c> trae todas, en
/// cualquier estado (las de Instagram también, para que el formulario las muestre con «Próximamente»); el número o el usuario visible
/// sólo lo trae <c>ListActiveAsync</c>, que resuelve los campos de la conexión, así que una conexión que no está activa viaja sin él.
/// </summary>
internal sealed class AutomationConnections(IMessagingConnectionDirectory directory) : IAutomationConnections
{
    public async Task<IReadOnlyList<AutomationConnectionRef>> ListAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var all = await directory.ListAllAsync(tenantId, cancellationToken);
        if (all.Count == 0)
        {
            return [];
        }

        var handles = (await directory.ListActiveAsync(tenantId, cancellationToken)).ToDictionary(connection => connection.Id, Handle);
        return
        [
            .. all.Select(connection => new AutomationConnectionRef(
                connection.Id, connection.Name, connection.Channel.ToString(), handles.GetValueOrDefault(connection.Id, string.Empty), connection.Status)),
        ];
    }

    private static string Handle(MessagingConnectionDto connection) =>
        connection.Channel == nameof(ConversationChannel.Instagram)
            ? string.IsNullOrWhiteSpace(connection.Username) ? string.Empty : $"@{connection.Username}"
            : connection.DisplayPhoneNumber ?? string.Empty;
}
