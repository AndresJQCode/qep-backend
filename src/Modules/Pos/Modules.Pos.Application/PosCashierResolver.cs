using BuildingBlocks.Application;
using Modules.Pos.Domain;
using Modules.Tenancy.Application;

namespace Modules.Pos.Application;

/// <summary>Subject autenticado → su membresía activa (copia de QuotationAdvisorResolver).</summary>
internal static class PosCashierResolver
{
    public static async Task<MemberId> ResolveAsync(
        IMembershipDirectory membershipDirectory,
        IExecutionContext executionContext,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var membershipId = await membershipDirectory.FindActiveMembershipIdAsync(
            executionContext.SubjectId, tenantId, cancellationToken);
        return membershipId is { } value
            ? new MemberId(value)
            : throw new RequestForbiddenException(
                "authorization.denied",
                "The subject does not have an active membership in this tenant.");
    }
}
