using Modules.Pipeline.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper;

/// <summary>Spec 2026-10-13 §4.2: el dueño de un negocio tiene que ser una membresía <c>Active</c> del tenant. Lee Tenancy por
/// <see cref="IMembershipRepository"/> (como <see cref="MessagingMemberNames"/>), que ya filtra por tenant; el estado se filtra acá
/// porque <c>ListByIdsAsync</c> devuelve cualquier estado.</summary>
internal sealed class PipelineActiveMembers(IMembershipRepository memberships) : IPipelineActiveMembers
{
    public async Task<IReadOnlySet<Guid>> FindActiveAsync(
        Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken)
    {
        if (memberIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var scoped = await memberships.ListByIdsAsync(
            new TenantId(tenantId), memberIds.Select(id => new MembershipId(id)).ToArray(), cancellationToken);
        return scoped
            .Where(membership => membership.State == MembershipState.Active)
            .Select(membership => membership.Id.Value)
            .ToHashSet();
    }
}
