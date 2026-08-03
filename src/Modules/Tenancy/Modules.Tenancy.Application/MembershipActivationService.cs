using BuildingBlocks.Application;
using Modules.Audit.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

public sealed class MembershipActivationService(
    IMembershipRepository membershipRepository,
    ITenancyUnitOfWork unitOfWork,
    IAuditRecorder auditRecorder,
    IOutboxWriter outboxWriter,
    IClock clock)
    : IMembershipActivation
{
    public async Task<IReadOnlyCollection<Guid>> AcceptInvitedMembershipsAsync(
        Guid userId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var invited = await membershipRepository.ListInvitedByUserAsync(userId, cancellationToken);
        var now = clock.UtcNow;

        foreach (var membership in invited)
        {
            try
            {
                membership.Accept(now);
            }
            catch (TenantDomainException)
            {
                // Expired invitation: Accept marked it Expired. Skip it; the user
                // simply has no active membership for that tenant.
                continue;
            }

            // The logging-in user is the actor of their own acceptance.
            auditRecorder.Record(
                membership.TenantId.Value,
                userId,
                "tenancy.membership.accepted",
                "membership",
                membership.Id.ToString(),
                "success",
                [],
                now);

            foreach (var domainEvent in membership.PullDomainEvents())
            {
                outboxWriter.Add(domainEvent, correlationId);
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        var activeTenants = await membershipRepository.ListActiveTenantsByUserAsync(
            userId,
            cancellationToken);
        return activeTenants.Select(tenant => tenant.Value).ToArray();
    }
}
