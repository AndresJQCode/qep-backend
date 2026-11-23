namespace Modules.Notifications.Application;

/// <summary>What the escalation needs to know about the ticket behind an assignment entry. <paramref name="IsOpen"/> is Support's call
/// (open and not resolved or closed); <paramref name="WhatsAppConnectionId"/> is the connection the tenant chose for task notices.</summary>
public sealed record TaskNoticeTicket(
    bool IsInternal, bool IsOpen, Guid? AssigneeMemberId, string TicketNumber, string Subject, Guid? WhatsAppConnectionId);

/// <summary>Task-notices spec 2026-11-22 §5.4: reads one ticket from Support. A single implementation lives in Bootstrapper (Notifications
/// does not reference Support). <c>null</c> when the ticket no longer exists.</summary>
public interface ITaskNoticeState
{
    Task<TaskNoticeTicket?> FindAsync(Guid tenantId, Guid ticketId, CancellationToken cancellationToken);
}

/// <summary>The member an escalation is for. <paramref name="PhoneE164"/> is the effective phone (own or HR) already normalized, or
/// <c>null</c> when there is none that can be trusted.</summary>
public sealed record TaskNoticeRecipient(bool IsActive, string DisplayName, string? Email, string? PhoneE164);

/// <summary>Reads the member to notify from Tenancy and HR. <c>null</c> when the membership does not exist.</summary>
public interface ITaskNoticeRecipients
{
    Task<TaskNoticeRecipient?> FindAsync(Guid tenantId, Guid memberId, CancellationToken cancellationToken);
}

/// <summary><paramref name="SentByMemberId"/> is who assigned: Messaging fills the fixed variable {{asesor}} with that member's name.
/// <paramref name="ClientId"/> is the escalation id, so a retried send is idempotent.</summary>
public sealed record TaskNoticeWhatsAppSend(
    Guid TenantId,
    Guid ConnectionId,
    Guid SentByMemberId,
    Guid ClientId,
    string PhoneE164,
    string RecipientName,
    string TicketNumber);

/// <summary>The outcome of a send. <see cref="Failed"/> carries the code stored in <c>channel_attempts</c>: <c>template_not_approved</c>,
/// <c>connection_unavailable:&lt;reason&gt;</c> or <c>send_failed:&lt;code&gt;</c>.</summary>
public abstract record TaskNoticeWhatsAppResult
{
    private TaskNoticeWhatsAppResult()
    {
    }

    public sealed record Sent : TaskNoticeWhatsAppResult;

    public sealed record Failed(string Code) : TaskNoticeWhatsAppResult;
}

/// <summary>Sends the approved <c>TaskNotice</c> template through Messaging. Implemented in Bootstrapper.</summary>
public interface ITaskNoticeWhatsApp
{
    Task<TaskNoticeWhatsAppResult> SendAsync(TaskNoticeWhatsAppSend send, CancellationToken cancellationToken);
}

/// <summary>Spec D-1: does the member have QEP open right now? Implemented in Bootstrapper over the app hub's presence. A failure to
/// answer surfaces as an exception: the worker treats it as «not connected» and escalates, it never stays silent.</summary>
public interface ITaskNoticePresence
{
    Task<bool> IsConnectedAsync(Guid tenantId, Guid memberId, CancellationToken cancellationToken);
}
