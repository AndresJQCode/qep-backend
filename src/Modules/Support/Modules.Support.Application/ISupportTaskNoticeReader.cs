namespace Modules.Support.Application;

/// <summary>
/// What the escalation of a task notice needs to know about the ticket right before sending (task-notices spec 2026-11-22 §5.7 and D-7):
/// whether it is still open and still of the same member, its number, subject (email only, never WhatsApp) and the WhatsApp connection
/// chosen in the tenant settings. It serves tasks and customer tickets alike; <see cref="IsInternal"/> tells them apart.
/// </summary>
public sealed record SupportTaskNoticeSnapshot(
    bool IsInternal,
    bool IsOpen,
    Guid? AssigneeMemberId,
    string TicketNumber,
    string Subject,
    Guid? WhatsAppConnectionId);

/// <summary>
/// The narrow read port Notifications reaches through <c>Bootstrapper</c>; Support exposes nothing else of its tickets to the notice
/// worker. Never writes.
/// </summary>
public interface ISupportTaskNoticeReader
{
    /// <summary>The ticket of the tenant, or <c>null</c> when it does not exist or belongs to another tenant.</summary>
    Task<SupportTaskNoticeSnapshot?> FindAsync(Guid tenantId, Guid ticketId, CancellationToken cancellationToken);
}
