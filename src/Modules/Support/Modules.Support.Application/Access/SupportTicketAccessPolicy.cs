using Modules.Support.Domain;

namespace Modules.Support.Application;

/// <summary>Los tres permisos de Soporte que deciden el acceso a un ticket, ya resueltos para un miembro (de los claims en HTTP, de la
/// base en el hub).</summary>
public sealed record SupportGrants(bool Read, bool Manage, bool TaskUse);

public enum SupportTicketAccessLevel
{
    /// <summary>El ticket no existe para quien pregunta: 404, nunca 403.</summary>
    None,

    /// <summary>Lo ve, no actúa.</summary>
    View,

    /// <summary>Lo ve y actúa.</summary>
    Act,
}

/// <summary>
/// La tabla de §5.6 del spec 2026-11-22 como función pura. Es la única: la puerta HTTP (<see cref="SupportTicketScope"/>) y el
/// autorizador del hub la usan, así que no pueden discrepar.
/// </summary>
public static class SupportTicketAccessPolicy
{
    /// <summary>
    /// <c>manage</c> ve y actúa sobre todo. <c>task.use</c> actúa sobre una tarea de la que el miembro es creador o responsable, aunque
    /// además lea (Review Focus 3: leer más usar tareas ve todo y actúa sólo en lo propio). <c>read</c> sin lo anterior ve todo y no
    /// actúa. Quien sólo usa tareas no ve ningún externo ni la tarea ajena; participar sin <c>task.use</c> no da nada.
    /// </summary>
    public static SupportTicketAccessLevel For(SupportGrants grants, SupportTicket ticket, Guid? memberId)
    {
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentNullException.ThrowIfNull(ticket);

        if (grants.Manage)
        {
            return SupportTicketAccessLevel.Act;
        }

        if (grants.TaskUse && ticket.IsInternal && IsParticipant(ticket, memberId))
        {
            return SupportTicketAccessLevel.Act;
        }

        return grants.Read ? SupportTicketAccessLevel.View : SupportTicketAccessLevel.None;
    }

    // Un miembro nulo (sin membresía activa) nunca es parte: un ticket sin responsable tiene AssigneeMemberId null y no puede
    // coincidir con él.
    private static bool IsParticipant(SupportTicket ticket, Guid? memberId) =>
        memberId is { } member && (ticket.CreatedByMemberId == member || ticket.AssigneeMemberId == member);
}
