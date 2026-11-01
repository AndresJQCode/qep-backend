using Microsoft.EntityFrameworkCore;
using Modules.Loans.Application;
using Modules.Loans.Domain;

namespace Modules.Loans.Infrastructure.Persistence;

internal sealed class LoanCapitalPaymentRepository(LoansDbContext dbContext) : ILoanCapitalPaymentRepository
{
    public void Add(LoanCapitalPayment payment) => dbContext.CapitalPayments.Add(payment);

    public Task<List<LoanCapitalPayment>> ListByLoanAsync(
        Guid tenantId, LoanId loanId, CancellationToken cancellationToken) =>
        dbContext.CapitalPayments
            .Where(payment => payment.TenantId == tenantId && payment.LoanId == loanId)
            .OrderBy(payment => payment.PaidOn)
            .ThenBy(payment => payment.CreatedAt)
            .ThenBy(payment => payment.Id)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<LoanCapitalPayment>> ListByLoansAsync(
        Guid tenantId, IReadOnlyCollection<LoanId> loanIds, CancellationToken cancellationToken)
    {
        if (loanIds.Count == 0)
        {
            return [];
        }

        var ids = loanIds.ToArray();
        return await dbContext.CapitalPayments
            .AsNoTracking()
            .Where(payment => payment.TenantId == tenantId && ids.Contains(payment.LoanId))
            .OrderBy(payment => payment.PaidOn)
            .ThenBy(payment => payment.CreatedAt)
            .ThenBy(payment => payment.Id)
            .ToListAsync(cancellationToken);
    }
}
