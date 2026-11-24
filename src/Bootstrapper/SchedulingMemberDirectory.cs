using Microsoft.EntityFrameworkCore;
using Modules.Scheduling.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;

namespace Bootstrapper;

/// <summary>
/// Las membresías que la Agenda puede nombrar: el miembro vinculado a un recurso y, por el usuario de la sesión, el alcance de quien no
/// tiene «ver todas las agendas». Calco de <see cref="HrMemberDirectory"/>. Sin caché; toda lectura va acotada al tenant, así que una
/// membresía de otro tenant no aparece.
/// </summary>
internal sealed class SchedulingMemberDirectory(TenancyDbContext tenancy) : ISchedulingMemberDirectory
{
    // El mismo rótulo de MembershipResponse.DisplayName nulo en el panel de Personas.
    private const string NoName = "Sin nombre";

    public async Task<IReadOnlyList<SchedulingMemberRef>> FindAsync(
        Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken)
    {
        if (memberIds.Count == 0)
        {
            return [];
        }

        var ids = memberIds.Distinct().Select(id => new MembershipId(id)).ToArray();
        var tenant = new TenantId(tenantId);
        var memberships = await tenancy.Memberships.AsNoTracking()
            .Where(membership => membership.TenantId == tenant && ids.Contains(membership.Id))
            .ToListAsync(cancellationToken);
        return [.. memberships.Select(ToRef)];
    }

    public async Task<SchedulingMemberRef?> FindBySubjectAsync(Guid tenantId, Guid subjectId, CancellationToken cancellationToken)
    {
        var tenant = new TenantId(tenantId);
        var membership = await tenancy.Memberships.AsNoTracking()
            .Where(candidate => candidate.TenantId == tenant && candidate.UserId == subjectId)
            .OrderBy(candidate => candidate.State)
            .FirstOrDefaultAsync(cancellationToken);
        return membership is null ? null : ToRef(membership);
    }

    private static SchedulingMemberRef ToRef(Membership membership) => new(
        membership.Id.Value,
        string.IsNullOrWhiteSpace(membership.DisplayName) ? NoName : membership.DisplayName,
        membership.State == MembershipState.Active);
}

/// <summary>El nombre visible del tenant: la sede principal que aprovisiona el primer acceso se llama como él (plan, corrección 12).</summary>
internal sealed class SchedulingTenantDirectory(ITenantDirectory tenants) : ISchedulingTenantDirectory
{
    public Task<string?> GetDisplayNameAsync(Guid tenantId, CancellationToken cancellationToken) =>
        tenants.GetDisplayNameAsync(new TenantId(tenantId), cancellationToken);
}
