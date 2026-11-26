using Modules.Tenancy.Application;

namespace Modules.Scheduling.Application;

/// <summary>
/// Resuelve el <see cref="ResourceScope"/> de quien pide (spec 2026-11-21, D-7; plan, corrección 7): con «ver todas las agendas» son todos
/// los recursos; sin él, sólo los vinculados a su membresía, que se buscan por el usuario de la sesión. Quien no tiene membresía en el
/// tenant no ve nada. Se resuelve en el servidor y lo aplica el repositorio: el cliente nunca filtra.
/// </summary>
public sealed class ResourceScopeResolver(
    IExecutionContext executionContext,
    ISchedulingMemberDirectory members,
    IResourceRepository resources)
{
    public async Task<ResourceScope> ResolveAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (SchedulingAuthorization.CanSeeEveryAgenda(executionContext))
        {
            return ResourceScope.All;
        }

        var member = await members.FindBySubjectAsync(tenantId, executionContext.SubjectId, cancellationToken);
        IReadOnlyList<Guid> linked = member is null
            ? []
            : await resources.ListIdsByMemberAsync(tenantId, member.MemberId, cancellationToken);
        return ResourceScope.For(readAll: false, linked);
    }
}
