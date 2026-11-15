using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

public sealed record ListSupportAssigneesQuery(Guid TenantId) : IQuery<SupportAssigneesDto>;

/// <summary>Los candidatos a responsable: exactamente las membresías activas cuyos roles conceden <c>support.ticket.manage</c> (el
/// mismo conjunto que se acepta al asignar), por nombre.</summary>
public sealed class ListSupportAssigneesHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ISupportMemberDirectory members)
    : IQueryHandler<ListSupportAssigneesQuery, SupportAssigneesDto>
{
    public async Task<SupportAssigneesDto> HandleAsync(ListSupportAssigneesQuery query, CancellationToken cancellationToken)
    {
        await SupportAuthorization.EnsureCanViewAsync(executionContext, tenantModules, query.TenantId, cancellationToken);

        var managers = await members.ListManagersAsync(query.TenantId, cancellationToken);

        // El único orden de la lista (revisión final, B3): invariante y con el id de desempate, para que no dependa de la cultura del
        // servidor ni de la base. Es el mismo comparador de ListLoanAssignees y del ListAssignees de ServiceContracts.
        return new SupportAssigneesDto(managers
            .OrderBy(member => member.DisplayName, StringComparer.InvariantCultureIgnoreCase)
            .ThenBy(member => member.MemberId)
            .Select(member => new SupportMemberDto(member.MemberId, member.DisplayName))
            .ToList());
    }
}
