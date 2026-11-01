using BuildingBlocks.Application;
using FluentValidation;
using Modules.Loans.Domain;
using Modules.Tenancy.Application;

namespace Modules.Loans.Application;

public sealed record UpdateInstallmentPaymentCommand(Guid TenantId, Guid LoanId, Guid InstallmentId, DateOnly PaidOn, string? Note)
    : ICommand<LoanDetailDto>, IInstallmentPaymentInput
{
    public static UpdateInstallmentPaymentCommand From(Guid tenantId, Guid loanId, Guid installmentId, PayInstallmentRequest request) =>
        new(tenantId, loanId, installmentId, request.PaidOn ?? default, request.Note);
}

public sealed class UpdateInstallmentPaymentValidator : AbstractValidator<UpdateInstallmentPaymentCommand>
{
    public UpdateInstallmentPaymentValidator() => InstallmentPaymentRules.Apply(this);
}

/// <summary>
/// Corrige la fecha y la nota de un pago. No cambia quién pagó ni cuántas cuotas pendientes hay, así que no toca el préstamo:
/// su <c>Version</c> queda como está.
/// </summary>
public sealed class UpdateInstallmentPaymentHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    ILoanRepository loans,
    ILoanInstallmentRepository installments,
    ILoansUnitOfWork unitOfWork,
    ILoansAuditPublisher auditPublisher,
    LoanDtoComposer composer,
    IValidator<UpdateInstallmentPaymentCommand> validator)
    : ICommandHandler<UpdateInstallmentPaymentCommand, LoanDetailDto>
{
    private static readonly string[] UpdatedFields = ["paidOn", "note"];

    public async Task<LoanDetailDto> HandleAsync(UpdateInstallmentPaymentCommand command, CancellationToken cancellationToken)
    {
        await LoansAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, LoansPermissions.Manage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var loan = await loans.FindAsync(command.TenantId, new LoanId(command.LoanId), cancellationToken)
            ?? throw LoansNotFound.Loan(command.LoanId);
        var installment = await installments.FindAsync(command.TenantId, loan.Id, command.InstallmentId, cancellationToken)
            ?? throw LoansNotFound.Installment(command.InstallmentId);

        var calendar = await tenantClock.GetAsync(command.TenantId, cancellationToken);
        var now = calendar.UtcNow;
        installment.UpdatePayment(command.PaidOn, command.Note, calendar.Today, now);

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            LoansAuditActions.InstallmentPaymentUpdated,
            LoansAuditActions.Resources.Installment,
            installment.Id.ToString(),
            "success",
            UpdatedFields,
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.ComposeDetailAsync(loan, calendar.Today, cancellationToken);
    }
}
