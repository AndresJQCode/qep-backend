namespace Modules.Support.Application;

/// <summary>
/// Task-notices spec 2026-11-22 §5.4: one WhatsApp connection of the tenant as the settings offer it for task notices, with the state of the
/// <c>TaskNotice</c> template on it. <see cref="PhoneNumber"/> is the visible number, null when the connection has none.
/// </summary>
public sealed record SupportTaskNoticeConnection(Guid ConnectionId, string Name, string? PhoneNumber, string TemplateStatus);

/// <summary>The <c>templateStatus</c> of <see cref="SupportTaskNoticeConnection"/>: a contract with the settings screen, never renamed.</summary>
public static class SupportTaskNoticeTemplateStatuses
{
    /// <summary>Meta approved it: the notice can leave through the connection.</summary>
    public const string Approved = "Approved";

    /// <summary>Submitted or in appeal: it may still be approved.</summary>
    public const string Pending = "Pending";

    /// <summary>Meta rejected it or paused it: needs a fix on the template screen.</summary>
    public const string Rejected = "Rejected";

    /// <summary>The connection has no <c>TaskNotice</c> template yet.</summary>
    public const string Missing = "Missing";
}

/// <summary>
/// The port Support declares for the WhatsApp side of the task notices: the connections it can choose from (Integrations) with the state of
/// the template on each (Messaging), and the global wait before a notice escalates. Implemented in <c>Bootstrapper</c>, so Support never
/// references the other modules.
/// </summary>
public interface ISupportTaskNoticeChannels
{
    /// <summary>The tenant active WhatsApp connections, with their template status, ordered by name and then id.</summary>
    Task<IReadOnlyList<SupportTaskNoticeConnection>> ListAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Minutes a notification can stay unread before the notice escalates (<c>Notifications:TaskNotice:EscalationAfterMinutes</c>).</summary>
    int EscalationAfterMinutes { get; }
}
