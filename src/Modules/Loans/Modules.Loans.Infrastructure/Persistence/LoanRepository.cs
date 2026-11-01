using Microsoft.EntityFrameworkCore;
using Modules.Loans.Application;
using Modules.Loans.Domain;

namespace Modules.Loans.Infrastructure.Persistence;

internal sealed class LoanRepository(LoansDbContext dbContext) : ILoanRepository
{
    public void Add(Loan loan) => dbContext.Loans.Add(loan);

    public void Remove(Loan loan) => dbContext.Loans.Remove(loan);

    public Task<Loan?> FindAsync(Guid tenantId, LoanId id, CancellationToken cancellationToken) =>
        dbContext.Loans.FirstOrDefaultAsync(
            loan => loan.TenantId == tenantId && loan.Id == id, cancellationToken);

    public async Task<(IReadOnlyList<Loan> Items, int Total)> ListAsync(
        LoanListFilter filter, CancellationToken cancellationToken)
    {
        var loans = dbContext.Loans
            .AsNoTracking()
            .Where(loan => loan.TenantId == filter.TenantId && loan.Status == filter.Status);

        var pattern = LikeSearch.ContainsPattern(filter.SearchTerm);
        if (pattern is not null)
        {
            var customerIds = filter.SearchCustomerIds is { Count: > 0 } ids ? ids.ToArray() : [];
            loans = loans.Where(loan =>
                customerIds.Contains(loan.CustomerId) ||
                EF.Functions.ILike(loan.Name, pattern, LikeSearch.EscapeCharacter));
        }

        var total = await loans.CountAsync(cancellationToken);
        var items = await loans
            .OrderByDescending(loan => loan.DisbursedOn)
            .ThenBy(loan => loan.Id)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public async Task<IReadOnlyList<Loan>> FindManyAsync(
        Guid tenantId, IReadOnlyCollection<LoanId> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var wanted = ids.ToArray();
        return await dbContext.Loans
            .AsNoTracking()
            .Where(loan => loan.TenantId == tenantId && wanted.Contains(loan.Id))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Loan>> ListAccruingAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await dbContext.Loans
            .AsNoTracking()
            .Where(loan => loan.TenantId == tenantId
                && loan.Status == LoanStatus.Active
                && loan.OutstandingPrincipal > 0)
            .OrderBy(loan => loan.DisbursedOn)
            .ThenBy(loan => loan.Id)
            .ToListAsync(cancellationToken);

    public async Task<LoanTotals> TotalsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var loans = dbContext.Loans.AsNoTracking().Where(loan => loan.TenantId == tenantId);
        var pending = dbContext.Installments
            .AsNoTracking()
            .Where(installment => installment.TenantId == tenantId && installment.Status == LoanInstallmentStatus.Pending);

        var byStatus = await loans
            .GroupBy(loan => loan.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        // Se agrupa en SQL sobre el tipo anónimo del Where, no sobre un record: EF no agrega sobre una proyección a record.
        // Nunca se suman monedas distintas: una línea por moneda.
        var outstanding = await loans
            .Where(loan => loan.Status == LoanStatus.Active)
            .GroupBy(loan => loan.Currency)
            .Select(group => new { Currency = group.Key, Amount = group.Sum(loan => loan.OutstandingPrincipal) })
            .OrderBy(group => group.Currency)
            .ToListAsync(cancellationToken);
        var pendingByCurrency = await pending
            .GroupBy(installment => installment.Currency)
            .Select(group => new { Currency = group.Key, Count = group.Count(), Amount = group.Sum(installment => installment.Amount) })
            .OrderBy(group => group.Currency)
            .ToListAsync(cancellationToken);

        return new LoanTotals(
            byStatus.Where(row => row.Status == LoanStatus.Active).Sum(row => row.Count),
            byStatus.Where(row => row.Status == LoanStatus.Settled).Sum(row => row.Count),
            pendingByCurrency.Sum(row => row.Count),
            outstanding.Select(row => new CurrencyTotal(row.Currency, row.Amount)).ToList(),
            pendingByCurrency.Select(row => new CurrencyTotal(row.Currency, row.Amount)).ToList());
    }
}
