using BuildingBlocks.Application;
using Modules.Loans.Domain;
using Modules.Tenancy.Application;

namespace Modules.Loans.Application;

public sealed record RemoveInstallmentPaymentCommand(Guid TenantId, Guid LoanId, Guid InstallmentId) : ICommand<LoanDetailDto>;

/// <summary>
/// Quita el pago y devuelve la cuota a pendiente (limpia fecha, nota, quién y cuándo). Hay una cuota pendiente más, así que un
/// préstamo saldado vuelve a activo; toca el préstamo y sube su <c>Version</c> por la misma razón que pagar.
/// </summary>
public sealed class RemoveInstallmentPaymentHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    ILoanRepository loans,
    ILoanInstallmentRepository installments,
    ILoansUnitOfWork unitOfWork,
    ILoansAuditPublisher auditPublisher,
    LoanDtoComposer composer)
    : ICommandHandler<RemoveInstallmentPaymentCommand, LoanDetailDto>
{
    public async Task<LoanDetailDto> HandleAsync(RemoveInstallmentPaymentCommand command, CancellationToken cancellationToken)
    {
        await LoansAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, LoansPermissions.Manage, cancellationToken);

        var loan = await loans.FindAsync(command.TenantId, new LoanId(command.LoanId), cancellationToken)
            ?? throw LoansNotFound.Loan(command.LoanId);
        var installment = await installments.FindAsync(command.TenantId, loan.Id, command.InstallmentId, cancellationToken)
            ?? throw LoansNotFound.Installment(command.InstallmentId);

        var calendar = await tenantClock.GetAsync(command.TenantId, cancellationToken);
        var now = calendar.UtcNow;
        var pendingBefore = await installments.CountPendingAsync(command.TenantId, loan.Id, cancellationToken);
        installment.RemovePayment();
        // Vuelve a pendiente: el conteo salió de la base antes de quitar el pago.
        loan.RefreshStatus(pendingBefore + 1, now);
        loan.Touch(now);

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            LoansAuditActions.InstallmentPaymentRemoved,
            LoansAuditActions.Resources.Installment,
            installment.Id.ToString(),
            "success",
            [],
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.ComposeDetailAsync(loan, calendar.Today, cancellationToken);
    }
}
