using BuildingBlocks.Application;
using Modules.Loans.Domain;
using Modules.Tenancy.Application;

namespace Modules.Loans.Application;

public sealed record DeleteLoanCommand(Guid TenantId, Guid LoanId) : ICommand<bool>;

/// <summary>
/// Borra el préstamo con sus cuotas pendientes (la base las cascadea). Una cuota pagada o una fila de abono, aunque esté anulada,
/// es historia que no se borra: <c>loans.loan.has_movement</c>.
/// </summary>
public sealed class DeleteLoanHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    ILoanRepository loans,
    ILoanInstallmentRepository installments,
    ILoanCapitalPaymentRepository capitalPayments,
    ILoansUnitOfWork unitOfWork,
    ILoansAuditPublisher auditPublisher)
    : ICommandHandler<DeleteLoanCommand, bool>
{
    public async Task<bool> HandleAsync(DeleteLoanCommand command, CancellationToken cancellationToken)
    {
        await LoansAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, LoansPermissions.Manage, cancellationToken);

        var loan = await loans.FindAsync(command.TenantId, new LoanId(command.LoanId), cancellationToken)
            ?? throw LoansNotFound.Loan(command.LoanId);
        var rows = await installments.ListByLoanAsync(command.TenantId, loan.Id, cancellationToken);
        var payments = await capitalPayments.ListByLoansAsync(command.TenantId, [loan.Id], cancellationToken);
        if (rows.Any(row => row.Status == LoanInstallmentStatus.Paid) || payments.Count > 0)
        {
            throw new LoansDomainException(
                LoansErrorCodes.LoanHasMovement, "A loan with a paid installment or a capital payment cannot be deleted.");
        }

        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;
        loans.Remove(loan);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            LoansAuditActions.LoanDeleted,
            LoansAuditActions.Resources.Loan,
            loan.Id.ToString(),
            "success",
            [],
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return true;
    }
}
