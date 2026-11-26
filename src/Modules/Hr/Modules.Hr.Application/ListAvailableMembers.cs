using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Hr.Application;

public sealed record ListAvailableMembersQuery(Guid TenantId) : IQuery<IReadOnlyList<MemberRefDto>>;

/// <summary>Las membresías activas del tenant que todavía no tienen un empleado vinculado, por nombre: el selector del alta.</summary>
public sealed class ListAvailableMembersHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IHrMemberDirectory members,
    IEmployeeRepository employees)
    : IQueryHandler<ListAvailableMembersQuery, IReadOnlyList<MemberRefDto>>
{
    public async Task<IReadOnlyList<MemberRefDto>> HandleAsync(ListAvailableMembersQuery query, CancellationToken cancellationToken)
    {
        await HrAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, cancellationToken, HrPermissions.EmployeeRead, HrPermissions.EmployeeManage);

        var linked = await employees.ListLinkedMemberIdsAsync(query.TenantId, cancellationToken);
        return (await members.ListActiveAsync(query.TenantId, cancellationToken))
            .Where(member => !linked.Contains(member.MemberId))
            .Select(member => new MemberRefDto(member.MemberId, member.DisplayName, member.IsActive))
            .ToList();
    }
}
