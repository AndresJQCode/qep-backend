using Modules.Identity.Application;
using Modules.Notifications.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper;

/// <summary>
/// Task-notices spec 2026-11-22 §5.4: who an escalation is for. Tenancy knows the state, the display name and the member's own phone (HR
/// supplies the fallback through <see cref="MembershipPhoneReader"/>); Identity knows the email. Notifications reaches both through this
/// single port. A membership that does not exist, or belongs to another tenant, is <c>null</c>; one that is not <c>Active</c> is returned
/// as inactive so the policy can name the reason.
/// </summary>
internal sealed class TaskNoticeRecipients(
    IMembershipRepository memberships, IUserDirectory users, MembershipPhoneReader phones) : ITaskNoticeRecipients
{
    public async Task<TaskNoticeRecipient?> FindAsync(Guid tenantId, Guid memberId, CancellationToken cancellationToken)
    {
        var found = await memberships.ListByIdsAsync(new TenantId(tenantId), [new MembershipId(memberId)], cancellationToken);
        if (found.SingleOrDefault() is not { } membership)
        {
            return null;
        }

        var email = await users.GetEmailAsync(membership.UserId, cancellationToken);
        var phone = await phones.ForAsync(membership, cancellationToken);
        var displayName = !string.IsNullOrWhiteSpace(membership.DisplayName) ? membership.DisplayName : email ?? "Miembro";
        return new TaskNoticeRecipient(membership.State == MembershipState.Active, displayName, email, phone.Phone);
    }
}
