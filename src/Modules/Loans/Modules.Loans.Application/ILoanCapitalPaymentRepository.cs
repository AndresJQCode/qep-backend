using Modules.Loans.Domain;

namespace Modules.Loans.Application;

public interface ILoanCapitalPaymentRepository
{
    void Add(LoanCapitalPayment payment);

    /// <summary>Con tracking y con los anulados: quien llama los rebalancea y guarda.</summary>
    Task<List<LoanCapitalPayment>> ListByLoanAsync(Guid tenantId, LoanId loanId, CancellationToken cancellationToken);

    Task<IReadOnlyList<LoanCapitalPayment>> ListByLoansAsync(
        Guid tenantId, IReadOnlyCollection<LoanId> loanIds, CancellationToken cancellationToken);
}
