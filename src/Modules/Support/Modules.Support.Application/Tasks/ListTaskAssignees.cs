using BuildingBlocks.Application;

namespace Modules.Support.Application;

public sealed record ListTaskAssigneesQuery(Guid TenantId) : IQuery<SupportAssigneesDto>;

/// <summary>Los candidatos a responsable de una tarea (spec 2026-11-22 §5.2): las membresías activas con <c>support.task.use</c> o
/// <c>support.ticket.manage</c>, el mismo conjunto que acepta el alta, por nombre. Lo ve quien puede crear o trabajar tareas: un
/// lector sin más permisos no.</summary>
public sealed class ListTaskAssigneesHandler(
    SupportTicketScope scope,
    ISupportMemberDirectory members)
    : IQueryHandler<ListTaskAssigneesQuery, SupportAssigneesDto>
{
    public async Task<SupportAssigneesDto> HandleAsync(ListTaskAssigneesQuery query, CancellationToken cancellationToken)
    {
        var access = await scope.EnsureAccessAsync(query.TenantId, cancellationToken);
        if (!(access.Grants.Manage || access.Grants.TaskUse))
        {
            throw SupportAuthorization.Denied();
        }

        var candidates = await members.ListTaskMembersAsync(query.TenantId, cancellationToken);

        // El mismo orden de ListSupportAssigneesHandler: invariante y con el id de desempate.
        return new SupportAssigneesDto(candidates
            .OrderBy(member => member.DisplayName, StringComparer.InvariantCultureIgnoreCase)
            .ThenBy(member => member.MemberId)
            .Select(member => new SupportMemberDto(member.MemberId, member.DisplayName, member.MemberId == access.MemberId))
            .ToList());
    }
}
