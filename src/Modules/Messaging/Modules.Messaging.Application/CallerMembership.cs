using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-10 D-A13 (P20): la membresía activa de quien llama en el tenant, resuelta una vez por request
/// (scoped). La comparten el filtro <c>assigned=me</c>, <c>counts.mine</c> y <c>assignedTo.isMe</c>.</summary>
public sealed class CallerMembership(IMembershipDirectory membershipDirectory, IExecutionContext executionContext)
{
    private Guid? _tenantId;
    private Guid? _memberId;

    public async Task<Guid?> FindAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (_tenantId != tenantId)
        {
            _memberId = await membershipDirectory.FindActiveMembershipIdAsync(executionContext.SubjectId, tenantId, cancellationToken);
            _tenantId = tenantId;
        }

        return _memberId;
    }
}
