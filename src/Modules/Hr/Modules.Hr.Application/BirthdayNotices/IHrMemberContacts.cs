namespace Modules.Hr.Application.BirthdayNotices;

/// <summary>Whether a member can be reached by each channel. Booleans only: HR never sees the number or the address.</summary>
public sealed record HrMemberContact(bool HasPhone, bool HasEmail);

/// <summary>
/// Port to the contact data of members (implemented in Bootstrapper over Tenancy, Identity and the HR phone fallback). It exists so
/// the settings screen can warn about a manager who would not receive a channel.
/// </summary>
public interface IHrMemberContacts
{
    /// <summary>One entry per found member of the tenant; an unknown or foreign id is simply absent.</summary>
    Task<IReadOnlyDictionary<Guid, HrMemberContact>> FindAsync(
        Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken);
}
