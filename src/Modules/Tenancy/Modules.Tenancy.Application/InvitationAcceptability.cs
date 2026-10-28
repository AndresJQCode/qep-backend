using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

/// <summary>
/// Same rejections as <c>Membership.Accept</c>, checked up front so a credential is never created
/// or verified for an invitation that cannot be accepted.
/// </summary>
public static class InvitationAcceptability
{
    public static void EnsurePending(InvitationDto invitation)
    {
        switch (invitation.Status)
        {
            case MembershipViewState.Pending:
                return;
            case MembershipViewState.Expired:
                throw new TenantDomainException(
                    "tenancy.membership.invitation_expired",
                    "The invitation has expired.");
            default:
                throw new TenantDomainException(
                    "tenancy.membership.not_invited",
                    "Only an invited membership can be accepted.");
        }
    }
}
