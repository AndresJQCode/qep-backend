using Modules.Support.Domain;

namespace Modules.Support.Application;

/// <summary>
/// Spec 2026-11-22 §5.4: el aviso de un ticket que cambió, ya decidido por su tipo. Un externo avisa en su tópico y en la bandeja; una
/// tarea, además, en <c>support:tasks:{m}</c> de su creador, su responsable y el responsable anterior (que ya no la verá en su lista y
/// necesita enterarse). Los handlers lo llaman en vez de <see cref="ISupportRealtime.TicketChangedAsync"/> para que ninguno decida solo.
/// </summary>
public static class SupportTicketNotices
{
    /// <param name="formerAssigneeMemberId">El responsable antes del cambio, leído antes de <c>Assign</c>; null si no cambió de manos.</param>
    public static Task ChangedAsync(
        ISupportRealtime realtime, SupportTicket ticket, Guid? formerAssigneeMemberId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(realtime);
        ArgumentNullException.ThrowIfNull(ticket);
        if (!ticket.IsInternal)
        {
            return realtime.TicketChangedAsync(ticket.TenantId, ticket.Id.Value, cancellationToken);
        }

        var members = new[] { ticket.CreatedByMemberId, ticket.AssigneeMemberId, formerAssigneeMemberId }
            .OfType<Guid>()
            .Distinct()
            .ToList();
        return realtime.TaskChangedAsync(ticket.TenantId, ticket.Id.Value, members, cancellationToken);
    }
}
