namespace Modules.Hr.Application.BirthdayNotices;

/// <summary>
/// Spec 2026-11-27: one active WhatsApp connection of the tenant, with the state of the <c>HrBirthdayNotice</c> template on it.
/// <see cref="PhoneNumber"/> is the visible number, null when the connection has none; <see cref="TemplateStatus"/> is one of
/// <see cref="HrBirthdayNoticeTemplateStatuses"/>.
/// </summary>
public sealed record HrWhatsAppConnection(Guid ConnectionId, string Name, string? PhoneNumber, string TemplateStatus);

/// <summary>The <c>templateStatus</c> of a connection in the birthday settings: a contract with the screen (the same values as the
/// Support task notices), never renamed.</summary>
public static class HrBirthdayNoticeTemplateStatuses
{
    /// <summary>Meta approved it: the digest can leave through the connection.</summary>
    public const string Approved = "Approved";

    /// <summary>Submitted or in appeal: it may still be approved.</summary>
    public const string Pending = "Pending";

    /// <summary>Meta rejected or paused it, or any other status: needs a fix on the template screen.</summary>
    public const string Rejected = "Rejected";

    /// <summary>The connection has no <c>HrBirthdayNotice</c> template yet.</summary>
    public const string Missing = "Missing";
}

/// <summary>
/// Port to the WhatsApp connections of a tenant (implemented in Bootstrapper over the Integrations connection directory and the
/// Messaging template mirror). HR knows nothing else about WhatsApp.
/// </summary>
public interface IHrWhatsAppConnections
{
    Task<bool> IsWhatsAppConnectionAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    /// <summary>The tenant active WhatsApp connections with their birthday template status, by name ignoring case and then by id.</summary>
    Task<IReadOnlyList<HrWhatsAppConnection>> ListAsync(Guid tenantId, CancellationToken cancellationToken);
}
