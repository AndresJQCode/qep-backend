using Microsoft.EntityFrameworkCore;
using Modules.Loans.Application;
using Modules.Loans.Domain;

namespace Modules.Loans.Infrastructure.Persistence;

internal sealed class LoanInstallmentRepository(LoansDbContext dbContext) : ILoanInstallmentRepository
{
    public void AddRange(IEnumerable<LoanInstallment> installments) => dbContext.Installments.AddRange(installments);

    public Task<LoanInstallment?> FindAsync(
        Guid tenantId, LoanId loanId, Guid installmentId, CancellationToken cancellationToken) =>
        dbContext.Installments
            .Include(installment => installment.Segments.OrderBy(segment => segment.Ordinal))
            .FirstOrDefaultAsync(
                installment => installment.TenantId == tenantId
                    && installment.LoanId == loanId
                    && installment.Id == installmentId,
                cancellationToken);

    public async Task<IReadOnlyList<LoanInstallment>> ListByLoanAsync(
        Guid tenantId, LoanId loanId, CancellationToken cancellationToken) =>
        await dbContext.Installments
            .AsNoTracking()
            .Include(installment => installment.Segments.OrderBy(segment => segment.Ordinal))
            .Where(installment => installment.TenantId == tenantId && installment.LoanId == loanId)
            .OrderByDescending(installment => installment.PeriodStart)
            .ToListAsync(cancellationToken);

    public Task<int> CountPendingAsync(Guid tenantId, LoanId loanId, CancellationToken cancellationToken) =>
        dbContext.Installments.CountAsync(
            installment => installment.TenantId == tenantId
                && installment.LoanId == loanId
                && installment.Status == LoanInstallmentStatus.Pending,
            cancellationToken);

    public async Task<IReadOnlyDictionary<LoanId, PendingTotal>> PendingTotalsAsync(
        Guid tenantId, IReadOnlyCollection<LoanId> loanIds, CancellationToken cancellationToken)
    {
        if (loanIds.Count == 0)
        {
            return new Dictionary<LoanId, PendingTotal>();
        }

        var ids = loanIds.ToArray();
        // Agrupa sobre el tipo anónimo y no sobre un record: EF no agrega sobre una proyección a record.
        var rows = await dbContext.Installments
            .AsNoTracking()
            .Where(installment => installment.TenantId == tenantId
                && installment.Status == LoanInstallmentStatus.Pending
                && ids.Contains(installment.LoanId))
            .GroupBy(installment => installment.LoanId)
            .Select(group => new { LoanId = group.Key, Count = group.Count(), Amount = group.Sum(installment => installment.Amount) })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(row => row.LoanId, row => new PendingTotal(row.Count, row.Amount));
    }

    public async Task<(IReadOnlyList<LoanInstallment> Items, int Total)> ListPendingAsync(
        PendingInstallmentFilter filter, CancellationToken cancellationToken)
    {
        var installments = dbContext.Installments
            .AsNoTracking()
            .Where(installment => installment.TenantId == filter.TenantId
                && installment.Status == LoanInstallmentStatus.Pending);

        var pattern = LikeSearch.ContainsPattern(filter.SearchTerm);
        if (pattern is not null)
        {
            var customerIds = filter.SearchCustomerIds is { Count: > 0 } ids ? ids.ToArray() : [];
            installments = installments.Where(installment => dbContext.Loans.Any(loan =>
                loan.Id == installment.LoanId &&
                loan.TenantId == installment.TenantId &&
                (customerIds.Contains(loan.CustomerId) ||
                    EF.Functions.ILike(loan.Name, pattern, LikeSearch.EscapeCharacter))));
        }

        var total = await installments.CountAsync(cancellationToken);
        var items = await installments
            .OrderBy(installment => installment.PeriodEnd)
            .ThenBy(installment => installment.Id)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public async Task<InstallmentTotals> PendingSummaryAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var pending = dbContext.Installments
            .AsNoTracking()
            .Where(installment => installment.TenantId == tenantId && installment.Status == LoanInstallmentStatus.Pending);

        var byCurrency = await pending
            .GroupBy(installment => installment.Currency)
            .Select(group => new { Currency = group.Key, Count = group.Count(), Amount = group.Sum(installment => installment.Amount) })
            .OrderBy(group => group.Currency)
            .ToListAsync(cancellationToken);

        var oldest = await pending
            .OrderBy(installment => installment.PeriodEnd)
            .ThenBy(installment => installment.Id)
            .Select(installment => new { installment.LoanId, installment.PeriodEnd, installment.Amount, installment.Currency })
            .FirstOrDefaultAsync(cancellationToken);

        return new InstallmentTotals(
            byCurrency.Sum(row => row.Count),
            byCurrency.Select(row => new CurrencyTotal(row.Currency, row.Amount)).ToList(),
            oldest is null ? null : new OldestInstallment(oldest.LoanId, oldest.PeriodEnd, oldest.Amount, oldest.Currency));
    }
}
