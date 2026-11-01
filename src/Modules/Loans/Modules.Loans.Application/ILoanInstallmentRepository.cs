using Modules.Loans.Domain;

namespace Modules.Loans.Application;

public interface ILoanInstallmentRepository
{
    void AddRange(IEnumerable<LoanInstallment> installments);

    /// <summary>Con tracking y con sus tramos: quien llama marca el pago y guarda.</summary>
    Task<LoanInstallment?> FindAsync(Guid tenantId, LoanId loanId, Guid installmentId, CancellationToken cancellationToken);

    /// <summary>Con tramos, sin tracking, de la más reciente a la más vieja.</summary>
    Task<IReadOnlyList<LoanInstallment>> ListByLoanAsync(Guid tenantId, LoanId loanId, CancellationToken cancellationToken);

    Task<int> CountPendingAsync(Guid tenantId, LoanId loanId, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<LoanId, PendingTotal>> PendingTotalsAsync(
        Guid tenantId, IReadOnlyCollection<LoanId> loanIds, CancellationToken cancellationToken);

    Task<(IReadOnlyList<LoanInstallment> Items, int Total)> ListPendingAsync(
        PendingInstallmentFilter filter, CancellationToken cancellationToken);

    Task<InstallmentTotals> PendingSummaryAsync(Guid tenantId, CancellationToken cancellationToken);
}
