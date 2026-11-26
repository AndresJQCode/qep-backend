using Modules.Authorization.Application;
using Modules.Identity.Application;
using Modules.Messaging.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper;

/// <summary>Spec 2026-10-10 §6.1.3 y §6.5: membresía del tenant + estado + roles → permisos, sin caché.</summary>
internal sealed class MessagingAssignees(IMembershipRepository memberships, IUserDirectory users, ITenantRoleCatalog roles)
    : IMessagingAssignees
{
    public async Task<bool> CanReplyAsync(Guid tenantId, Guid memberId, CancellationToken cancellationToken)
    {
        var membership = await memberships.FindByIdAsync(new MembershipId(memberId), new TenantId(tenantId), cancellationToken);
        return membership is { State: MembershipState.Active } && await GrantsManageAsync(tenantId, membership, cancellationToken);
    }

    public async Task<IReadOnlyList<AssigneeDto>> ListAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var eligible = new List<Membership>();
        foreach (var membership in await memberships.ListByTenantAsync(new TenantId(tenantId), cancellationToken))
        {
            if (membership.State == MembershipState.Active && await GrantsManageAsync(tenantId, membership, cancellationToken))
            {
                eligible.Add(membership);
            }
        }

        // Un solo viaje a Identity por los correos de quienes no tienen nombre, como MessagingMemberNames.
        var emails = await users.GetEmailsAsync(
            eligible.Where(membership => string.IsNullOrWhiteSpace(membership.DisplayName))
                .Select(membership => membership.UserId)
                .ToList(),
            cancellationToken);
        var result = new List<AssigneeDto>();
        foreach (var membership in eligible)
        {
            // P17: como MessagingMemberNames, el nombre o el correo.
            var name = !string.IsNullOrWhiteSpace(membership.DisplayName)
                ? membership.DisplayName
                : emails.GetValueOrDefault(membership.UserId);
            if (!string.IsNullOrWhiteSpace(name))
            {
                result.Add(new AssigneeDto(membership.Id.Value, name));
            }
        }

        return result.OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private async Task<bool> GrantsManageAsync(Guid tenantId, Membership membership, CancellationToken cancellationToken) =>
        (await roles.PermissionsForAsync(tenantId, membership.Roles, cancellationToken)).Contains(MessagingPermissions.ConversationManage, StringComparer.Ordinal);
}
