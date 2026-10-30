using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.ServiceContracts.Application;

public sealed record ListAssigneesQuery(Guid TenantId) : IQuery<AssigneesDto>;

/// <summary>
/// Los candidatos a encargado: exactamente las membresías activas cuyos roles conceden <c>billing.service.manage</c> (el mismo
/// conjunto que <see cref="ProfileRules.EnsureAssigneeCanManage"/> acepta al guardar el perfil), por nombre.
/// </summary>
public sealed class ListAssigneesHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IServiceContractsMemberDirectory members)
    : IQueryHandler<ListAssigneesQuery, AssigneesDto>
{
    public async Task<AssigneesDto> HandleAsync(ListAssigneesQuery query, CancellationToken cancellationToken)
    {
        await ServiceContractsAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, ServiceContractsPermissions.Read, cancellationToken);

        var managers = await members.ListManagersAsync(query.TenantId, cancellationToken);

        // Invariante y con el id de desempate: el orden no depende de la cultura del servidor ni de la base.
        return new AssigneesDto(managers
            .OrderBy(member => member.DisplayName, StringComparer.InvariantCultureIgnoreCase)
            .ThenBy(member => member.MemberId)
            .Select(member => new MemberRefDto(member.MemberId, member.DisplayName, member.IsActive))
            .ToList());
    }
}
