using Modules.Notifications.Domain;

namespace Modules.Notifications.Application;

/// <summary>The channel an escalation takes. Exactly one is ever used.</summary>
public abstract record TaskNoticeRoute
{
    private TaskNoticeRoute()
    {
    }

    public sealed record WhatsApp(Guid ConnectionId, string PhoneE164) : TaskNoticeRoute;

    public sealed record EmailOnly(string Reason) : TaskNoticeRoute;
}

/// <summary>
/// Task-notices spec 2026-11-22 §5.4: the two pure decisions of the escalation worker, kept apart from I/O so each branch is a
/// unit test.
/// </summary>
public static class TaskNoticePolicy
{
    /// <summary>Spec D-1: while the member keeps QEP open the escalation waits this long and looks again.</summary>
    public static readonly TimeSpan PresenceRecheck = TimeSpan.FromMinutes(1);

    /// <returns>Why the notice must not go out, or <c>null</c> when it must. The order is part of the contract: a resolved entry reports
    /// <c>resolved</c> even if it was also read, and an inactive member comes last.</returns>
    public static string? SkipReason(
        bool entryRead, bool entryResolved, TaskNoticeTicket? ticket, Guid memberId, TaskNoticeRecipient? recipient) =>
        SkipReason(entryRead, entryResolved, ticket, memberId) ?? MemberSkipReason(recipient);

    /// <summary>
    /// The checks that need only the entry and the ticket, which the worker already holds. They come first so a row that is going to be
    /// skipped never costs a presence query or a recipient lookup (each one a call into another module).
    /// </summary>
    public static string? SkipReason(bool entryRead, bool entryResolved, TaskNoticeTicket? ticket, Guid memberId)
    {
        if (entryResolved)
        {
            return TaskNoticeSkipReasons.Resolved;
        }

        if (entryRead)
        {
            return TaskNoticeSkipReasons.Read;
        }

        // Spec D-7: a customer ticket is escalated like a task, so only a ticket that is gone counts as missing.
        if (ticket is null)
        {
            return TaskNoticeSkipReasons.TicketMissing;
        }

        if (!ticket.IsOpen)
        {
            return TaskNoticeSkipReasons.TaskClosed;
        }

        if (ticket.AssigneeMemberId != memberId)
        {
            return TaskNoticeSkipReasons.NotAssignee;
        }

        return null;
    }

    /// <summary>The last check, asked only once the row is about to be sent: the recipient is the most expensive thing to resolve.</summary>
    public static string? MemberSkipReason(TaskNoticeRecipient? recipient) =>
        recipient is not { IsActive: true } ? TaskNoticeSkipReasons.MemberInactive : null;

    /// <summary>Spec D-5: WhatsApp only with a connection chosen for the tenant and a trusted phone; otherwise email.</summary>
    public static TaskNoticeRoute Route(Guid? connectionId, string? phoneE164)
    {
        if (connectionId is not { } connection)
        {
            return new TaskNoticeRoute.EmailOnly("no_connection");
        }

        return string.IsNullOrWhiteSpace(phoneE164)
            ? new TaskNoticeRoute.EmailOnly("no_phone")
            : new TaskNoticeRoute.WhatsApp(connection, phoneE164);
    }
}
