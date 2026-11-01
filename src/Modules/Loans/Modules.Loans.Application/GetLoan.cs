using BuildingBlocks.Application;
using Modules.Loans.Domain;
using Modules.Tenancy.Application;

namespace Modules.Loans.Application;

public sealed record GetLoanQuery(Guid TenantId, Guid LoanId) : IQuery<LoanDetailDto>;

public sealed class GetLoanHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    ILoanRepository loans,
    LoanDtoComposer composer)
    : IQueryHandler<GetLoanQuery, LoanDetailDto>
{
    public async Task<LoanDetailDto> HandleAsync(GetLoanQuery query, CancellationToken cancellationToken)
    {
        await LoansAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, LoansPermissions.Read, cancellationToken);

        // Acotado al tenant de la ruta: el préstamo de otro tenant no es «no tuyo», es «no existe».
        var loan = await loans.FindAsync(query.TenantId, new LoanId(query.LoanId), cancellationToken)
            ?? throw LoansNotFound.Loan(query.LoanId);
        var today = (await tenantClock.GetAsync(query.TenantId, cancellationToken)).Today;

        return await composer.ComposeDetailAsync(loan, today, cancellationToken);
    }
}
