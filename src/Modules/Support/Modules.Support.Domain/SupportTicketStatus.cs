namespace Modules.Support.Domain;

/// <summary>Spec 2026-11-14 §7.1. Se persisten y viajan por nombre: el diccionario de textos lo tiene el frontend.</summary>
public enum SupportTicketStatus
{
    New,
    Open,
    WaitingOnCustomer,
    WaitingInternal,
    Resolved,
    Closed,
}

/// <summary>La máquina de estados del ticket (spec §7.1), en un solo lugar: el selector de estado de la pantalla la recibe en
/// <c>AllowedTransitions</c> y no la repite.</summary>
public static class SupportTicketStatuses
{
    public static IReadOnlyList<SupportTicketStatus> Ordered { get; } =
    [
        SupportTicketStatus.New,
        SupportTicketStatus.Open,
        SupportTicketStatus.WaitingOnCustomer,
        SupportTicketStatus.WaitingInternal,
        SupportTicketStatus.Resolved,
        SupportTicketStatus.Closed,
    ];

    // Ningún estado vuelve a New, y Closed es terminal.
    private static readonly Dictionary<SupportTicketStatus, SupportTicketStatus[]> Transitions = new()
    {
        [SupportTicketStatus.New] = [SupportTicketStatus.Open, SupportTicketStatus.WaitingOnCustomer, SupportTicketStatus.Resolved],
        [SupportTicketStatus.Open] = [SupportTicketStatus.WaitingOnCustomer, SupportTicketStatus.WaitingInternal, SupportTicketStatus.Resolved],
        [SupportTicketStatus.WaitingOnCustomer] =
            [SupportTicketStatus.Open, SupportTicketStatus.WaitingInternal, SupportTicketStatus.Resolved, SupportTicketStatus.Closed],
        [SupportTicketStatus.WaitingInternal] = [SupportTicketStatus.Open, SupportTicketStatus.WaitingOnCustomer, SupportTicketStatus.Resolved],
        [SupportTicketStatus.Resolved] = [SupportTicketStatus.Closed, SupportTicketStatus.Open],
        [SupportTicketStatus.Closed] = [],
    };

    public static IReadOnlyList<SupportTicketStatus> AllowedFrom(SupportTicketStatus from) => Transitions[from];

    /// <summary>La tabla de <see cref="AllowedFrom"/>; una tarea interna no tiene cliente al que esperar, así que no ofrece
    /// <see cref="SupportTicketStatus.WaitingOnCustomer"/>.</summary>
    public static IReadOnlyList<SupportTicketStatus> AllowedFor(SupportTicketStatus from, bool isInternal) =>
        isInternal
            ? Transitions[from].Where(status => status is not SupportTicketStatus.WaitingOnCustomer).ToArray()
            : Transitions[from];

    /// <summary>El reloj se detiene esperando al cliente y al terminar. <see cref="SupportTicketStatus.WaitingInternal"/> no lo
    /// detiene: esperar a otra área de la empresa no es culpa del solicitante.</summary>
    public static bool StopsTheClock(SupportTicketStatus status) =>
        status is SupportTicketStatus.WaitingOnCustomer or SupportTicketStatus.Resolved or SupportTicketStatus.Closed;

    /// <summary>Spec 2026-11-22 §7 and D-15: the statuses in which somebody has to act, so a ticket without an assignee in one of them
    /// is «sin asignar» for the alert: <see cref="SupportTicketStatus.New"/>, <see cref="SupportTicketStatus.Open"/> and
    /// <see cref="SupportTicketStatus.WaitingInternal"/>. Waiting on the customer, resolved and closed do not count.</summary>
    public static bool CountsAsUnassigned(SupportTicketStatus status) =>
        status is SupportTicketStatus.New or SupportTicketStatus.Open or SupportTicketStatus.WaitingInternal;
}
