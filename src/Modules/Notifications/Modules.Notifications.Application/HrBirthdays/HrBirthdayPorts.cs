namespace Modules.Notifications.Application;

/// <summary>
/// HR birthday notices, spec 2026-11-26 §5.3: what the WhatsApp adapter needs to send one digest. <paramref name="SentByMemberId"/> is the author
/// the Messaging row requires (the member who last saved the setting, carried by the event); <paramref name="ClientId"/> is the delivery id, so a
/// retried send is idempotent. <paramref name="Today"/>, <paramref name="UpcomingDate"/> and <paramref name="Upcoming"/> are already the texts of
/// the template variables (<see cref="HrBirthdayWhatsAppText"/>): names and a date, never a birth year or an age.
/// </summary>
public sealed record HrBirthdayWhatsAppSend(
    Guid TenantId,
    Guid ConnectionId,
    Guid SentByMemberId,
    Guid ClientId,
    string PhoneE164,
    string RecipientName,
    string Today,
    string UpcomingDate,
    string Upcoming);

/// <summary>Sends the approved <c>HrBirthdayNotice</c> template through Messaging. Implemented in Bootstrapper (Notifications does not reference
/// Messaging). It reuses <see cref="TaskNoticeWhatsAppResult"/> and its codes: <c>template_not_approved</c>,
/// <c>connection_unavailable:&lt;reason&gt;</c> and <c>send_failed:&lt;code&gt;</c>.</summary>
public interface IHrBirthdayWhatsApp
{
    Task<TaskNoticeWhatsAppResult> SendAsync(HrBirthdayWhatsAppSend send, CancellationToken cancellationToken);
}

/// <summary>Whether a manager can still read HR data. Checked right before a digest is sent, because the digest carries employee names and the
/// permission may have been revoked after the run enqueued it. Implemented in Bootstrapper (Tenancy roles); Notifications does not reference HR.</summary>
public interface IHrBirthdayAccess
{
    /// <returns><c>true</c> when the member is active in the tenant and holds <c>hr.employee.read</c>.</returns>
    Task<bool> CanReadEmployeesAsync(Guid tenantId, Guid memberId, CancellationToken cancellationToken);
}
