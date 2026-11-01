using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Loans.Application;

public sealed record ListLoanAssigneesQuery(Guid TenantId) : IQuery<LoanAssigneesDto>;

/// <summary>
/// Los candidatos a encargado: exactamente las membresías activas cuyos roles conceden <c>loans.loan.manage</c> (el mismo conjunto
/// que se acepta al guardar un préstamo), por nombre.
/// </summary>
public sealed class ListLoanAssigneesHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ILoansMemberDirectory members)
    : IQueryHandler<ListLoanAssigneesQuery, LoanAssigneesDto>
{
    public async Task<LoanAssigneesDto> HandleAsync(ListLoanAssigneesQuery query, CancellationToken cancellationToken)
    {
        await LoansAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, LoansPermissions.Read, cancellationToken);

        var managers = await members.ListManagersAsync(query.TenantId, cancellationToken);

        // Invariante y con el id de desempate: el orden no depende de la cultura del servidor ni de la base.
        return new LoanAssigneesDto(managers
            .OrderBy(member => member.DisplayName, StringComparer.InvariantCultureIgnoreCase)
            .ThenBy(member => member.MemberId)
            .Select(member => new LoanMemberDto(member.MemberId, member.DisplayName, member.IsActive))
            .ToList());
    }
}