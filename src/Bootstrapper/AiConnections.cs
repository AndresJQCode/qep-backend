using Modules.Ai.Application;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-10-29 D-A3: el único punto donde Ai ve las conexiones de Mensajería, copia de <see cref="AutomationConnections"/>. Trae
/// todas, en cualquier estado (Instagram también, para que el editor la muestre deshabilitada); el número o el usuario visible sólo lo
/// trae <c>ListActiveAsync</c>, así que una conexión que no está activa viaja sin él.
/// </summary>
internal sealed class AiConnections(IMessagingConnectionDirectory directory) : IAiConnections
{
    public async Task<IReadOnlyList<AiConnectionRef>> ListAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var all = await directory.ListAllAsync(tenantId, cancellationToken);
        if (all.Count == 0)
        {
            return [];
        }

        var handles = (await directory.ListActiveAsync(tenantId, cancellationToken)).ToDictionary(connection => connection.Id, Handle);
        return
        [
            .. all.Select(connection => new AiConnectionRef(
                connection.Id, connection.Name, connection.Channel.ToString(), handles.GetValueOrDefault(connection.Id, string.Empty), connection.Status)),
        ];
    }

    // Existe por el autoguardado del borrador, que valida en cada pausa al escribir: ListActiveAsync resuelve cada conexión activa con
    // su secreto, y para contar las conexiones vivas bastan los ids.
    public async Task<IReadOnlySet<Guid>> ListIdsAsync(Guid tenantId, CancellationToken cancellationToken) =>
        (await directory.ListAllAsync(tenantId, cancellationToken)).Select(connection => connection.Id).ToHashSet();

    private static string Handle(MessagingConnectionDto connection) =>
        connection.Channel == nameof(ConversationChannel.Instagram)
            ? string.IsNullOrWhiteSpace(connection.Username) ? string.Empty : $"@{connection.Username}"
            : connection.DisplayPhoneNumber ?? string.Empty;
}

/// <summary>Los destinos posibles de un traspaso: los mismos miembros que ofrece «Transferir», copia de <see cref="AutomationMembers"/>.</summary>
internal sealed class AiMembers(IMessagingAssignees assignees) : IAiMembers
{
    public async Task<IReadOnlyList<AiMemberRef>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
        [.. (await assignees.ListAsync(tenantId, cancellationToken)).Select(assignee => new AiMemberRef(assignee.MemberId, assignee.DisplayName))];
}
