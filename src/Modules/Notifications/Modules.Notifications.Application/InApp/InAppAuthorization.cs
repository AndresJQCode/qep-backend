using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Notifications.Application;

/// <summary>
/// Spec 2026-11-22 §8: the bell has no permission of its own (each member reads what is theirs), so the double layer is the tenant of the
/// route against the caller's, and an active membership. Both answer 403 <c>authorization.denied</c>, never 404: a 404 would confirm that
/// the id exists in another tenant.
/// </summary>
internal static class InAppAuthorization
{
    public static async Task<Guid> EnsureMemberAsync(
        IExecutionContext executionContext, IMembershipDirectory memberships, Guid tenantId, CancellationToken cancellationToken)
    {
        if (executionContext.TenantId.Value != tenantId)
        {
            throw Denied();
        }

        return await memberships.FindActiveMembershipIdAsync(executionContext.SubjectId, tenantId, cancellationToken) ?? throw Denied();
    }

    private static RequestForbiddenException Denied() =>
        new("authorization.denied", "The subject cannot read or change notifications for this tenant.");
}
