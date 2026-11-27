using Modules.Hr.Application.BirthdayNotices;
using Modules.Identity.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-11-26 §5.1: whether each birthday manager can be reached by phone and by email, so the settings screen can warn. Same
/// sources as <see cref="TaskNoticeRecipients"/>: Tenancy for the member and its phone (HR supplies the fallback through
/// <see cref="MembershipPhoneReader"/>), Identity for the email. Only booleans leave: HR never sees the number or the address.
/// </summary>
internal sealed class HrMemberContacts(
    IMembershipRepository memberships, IUserDirectory users, MembershipPhoneReader phones) : IHrMemberContacts
{
    public async Task<IReadOnlyDictionary<Guid, HrMemberContact>> FindAsync(
        Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, HrMemberContact>();
        if (memberIds.Count == 0)
        {
            return result;
        }

        var ids = memberIds.Distinct().Select(id => new MembershipId(id)).ToArray();
        var found = await memberships.ListByIdsAsync(new TenantId(tenantId), ids, cancellationToken);
        var effectivePhones = await phones.ForAsync(found, cancellationToken);
        foreach (var membership in found)
        {
            var email = await users.GetEmailAsync(membership.UserId, cancellationToken);
            result[membership.Id.Value] = new HrMemberContact(
                effectivePhones[membership.Id].Phone is not null, !string.IsNullOrWhiteSpace(email));
        }

        return result;
    }
}
