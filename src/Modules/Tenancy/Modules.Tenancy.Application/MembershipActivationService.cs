using BuildingBlocks.Application;
using Modules.Audit.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

public sealed class MembershipActivationService(
    IMembershipRepository membershipRepository,
    ITenancyUnitOfWork unitOfWork,
    IAuditRecorder auditRecorder,
    IOutboxWriter outboxWriter,
    IClock clock,
    ITenantAccessVersion tenantAccessVersion)
    : IMembershipActivation
{
    public async Task<IReadOnlyCollection<Guid>> AcceptInvitedMembershipsAsync(
        Guid userId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var invited = await membershipRepository.ListInvitedByUserAsync(userId, cancellationToken);
        var now = clock.UtcNow;
        var accepted = new List<Guid>();

        foreach (var membership in invited)
        {
            try
            {
                membership.Accept(now);
            }
            catch (TenantDomainException)
            {
                // Invitación vencida: Accept la marcó Expired. Se saltea; el usuario
                // simplemente no tiene membresía activa para ese tenant.
                continue;
            }

            accepted.Add(membership.TenantId.Value);

            // El usuario que hace login es el actor de su propia aceptación.
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

        // Spec 2026-11-13, D-5: el «sin membresía» cacheado de cada tenant aceptado se descarta, antes del guardado y otra vez
        // después. Una invitación vencida no concede nada: no sube.
        await BumpAsync(accepted, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await BumpAsync(accepted, cancellationToken);

        var activeTenants = await membershipRepository.ListActiveTenantsByUserAsync(
            userId,
            cancellationToken);
        return activeTenants.Select(tenant => tenant.Value).ToArray();
    }

    private async Task BumpAsync(IEnumerable<Guid> tenantIds, CancellationToken cancellationToken)
    {
        foreach (var tenantId in tenantIds.Distinct())
        {
            await tenantAccessVersion.BumpAsync(tenantId, cancellationToken);
        }
    }
}
