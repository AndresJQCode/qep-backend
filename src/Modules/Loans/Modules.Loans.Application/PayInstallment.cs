using BuildingBlocks.Application;
using FluentValidation;
using Modules.Loans.Domain;
using Modules.Tenancy.Application;

namespace Modules.Loans.Application;

/// <summary>Cuerpo de <c>POST …/pay</c> y <c>PUT …/payment</c>; la fecha es nullable para que su ausencia sea <c>validation.failed</c> con el campo y no un 400 del binding.</summary>
public sealed record PayInstallmentRequest(DateOnly? PaidOn, string? Note);

public interface IInstallmentPaymentInput
{
    DateOnly PaidOn { get; }

    string? Note { get; }
}

public sealed record PayInstallmentCommand(Guid TenantId, Guid LoanId, Guid InstallmentId, DateOnly PaidOn, string? Note)
    : ICommand<LoanDetailDto>, IInstallmentPaymentInput
{
    public static PayInstallmentCommand From(Guid tenantId, Guid loanId, Guid installmentId, PayInstallmentRequest request) =>
        new(tenantId, loanId, installmentId, request.PaidOn ?? default, request.Note);
}

internal static class InstallmentPaymentRules
{
    /// <summary>La fecha es obligatoria y la nota, ya recortada, no pasa de 1000 caracteres; la fecha futura la juzga el dominio con la «hoy» del tenant.</summary>
    public static void Apply<T>(AbstractValidator<T> validator)
        where T : IInstallmentPaymentInput
    {
        validator.RuleFor(command => command.PaidOn).NotEqual(default(DateOnly));
        validator.RuleFor(command => command.Note)
            .Must(note => note is null || note.Trim().Length <= LoanInstallment.PaymentNoteMaxLength)
            .WithMessage($"The note cannot exceed {LoanInstallment.PaymentNoteMaxLength} characters.");
    }
}

public sealed class PayInstallmentValidator : AbstractValidator<PayInstallmentCommand>
{
    public PayInstallmentValidator() => InstallmentPaymentRules.Apply(this);
}

/// <summary>
/// Marca una cuota como pagada: autorización → validador → préstamo → cuota → membresía activa (sin ella no hay a quién atribuirlo,
/// 403) → «hoy» del tenant → dominio → estado del préstamo → auditoría → guardar → detalle. Cualquier cuota pendiente se puede pagar,
/// en cualquier orden. Pagar toca el préstamo a propósito: sube su <c>Version</c> y así choca con un abono en curso o con otro pago
/// de la última cuota.
/// </summary>
public sealed class PayInstallmentHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    IMembershipDirectory membershipDirectory,
    ILoanRepository loans,
    ILoanInstallmentRepository installments,
    ILoansUnitOfWork unitOfWork,
    ILoansAuditPublisher auditPublisher,
    LoanDtoComposer composer,
    IValidator<PayInstallmentCommand> validator)
    : ICommandHandler<PayInstallmentCommand, LoanDetailDto>
{
    private static readonly string[] PaidFields = ["paidOn", "note"];

    public async Task<LoanDetailDto> HandleAsync(PayInstallmentCommand command, CancellationToken cancellationToken)
    {
        await LoansAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, LoansPermissions.Manage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var loan = await loans.FindAsync(command.TenantId, new LoanId(command.LoanId), cancellationToken)
            ?? throw LoansNotFound.Loan(command.LoanId);
        var installment = await installments.FindAsync(command.TenantId, loan.Id, command.InstallmentId, cancellationToken)
            ?? throw LoansNotFound.Installment(command.InstallmentId);
        var paidBy = await membershipDirectory.FindActiveMembershipIdAsync(
                executionContext.SubjectId, command.TenantId, cancellationToken)
            ?? throw LoansAuthorization.Denied();

        var calendar = await tenantClock.GetAsync(command.TenantId, cancellationToken);
        var now = calendar.UtcNow;
        var pendingBefore = await installments.CountPendingAsync(command.TenantId, loan.Id, cancellationToken);
        installment.MarkPaid(command.PaidOn, command.Note, paidBy, calendar.Today, now);
        // Ya no es pendiente: el conteo salió de la base antes de marcarla.
        loan.RefreshStatus(pendingBefore - 1, now);
        loan.Touch(now);

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            LoansAuditActions.InstallmentPaid,
            LoansAuditActions.Resources.Installment,
            installment.Id.ToString(),
            "success",
            PaidFields,
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.ComposeDetailAsync(loan, calendar.Today, cancellationToken);
    }
}
