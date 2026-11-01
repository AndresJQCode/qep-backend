using Modules.Loans.Domain;

namespace Modules.Loans.Application;

public interface ILoanRepository
{
    void Add(Loan loan);

    void Remove(Loan loan);

    /// <summary>Con tracking: quien llama lo muta y guarda.</summary>
    Task<Loan?> FindAsync(Guid tenantId, LoanId id, CancellationToken cancellationToken);

    Task<(IReadOnlyList<Loan> Items, int Total)> ListAsync(LoanListFilter filter, CancellationToken cancellationToken);

    Task<IReadOnlyList<Loan>> FindManyAsync(Guid tenantId, IReadOnlyCollection<LoanId> ids, CancellationToken cancellationToken);

    /// <summary>Los préstamos que todavía causan interés: <c>Active</c> y con capital pendiente. Sin tracking.</summary>
    Task<IReadOnlyList<Loan>> ListAccruingAsync(Guid tenantId, CancellationToken cancellationToken);

    Task<LoanTotals> TotalsAsync(Guid tenantId, CancellationToken cancellationToken);
}
