using BuildingBlocks.Application;
using FluentValidation;
using Modules.Loans.Domain;
using Modules.Tenancy.Application;

namespace Modules.Loans.Application;

/// <summary>Cuerpo de <c>POST …/void</c>; anulable para que su ausencia sea <c>validation.failed</c> con el campo.</summary>
public sealed record VoidCapitalPaymentRequest(string? Reason);

public sealed record VoidCapitalPaymentCommand(Guid TenantId, Guid LoanId, Guid PaymentId, string Reason) : ICommand<LoanDetailDto>
{
    public static VoidCapitalPaymentCommand From(Guid tenantId, Guid loanId, Guid paymentId, VoidCapitalPaymentRequest request) =>
        new(tenantId, loanId, paymentId, request.Reason ?? string.Empty);
}

public sealed class VoidCapitalPaymentValidator : AbstractValidator<VoidCapitalPaymentCommand>
{
    public VoidCapitalPaymentValidator() =>
        // El motivo es obligatorio y se mide recortado, como lo guarda el dominio.
        RuleFor(command => command.Reason)
            .Must(reason => reason?.Trim() is { Length: > 0 and <= LoanCapitalPayment.VoidReasonMaxLength })
            .WithMessage($"The reason is required and cannot exceed {LoanCapitalPayment.VoidReasonMaxLength} characters.");
}

/// <summary>
/// Anula el abono más reciente: autorización → validador → «hoy» del tenant → carga (genera los períodos cerrados) → guarda lo
/// generado → dominio → auditoría → guardar → detalle. Lo generado se guarda antes por la misma razón que al abonar. La fila no se borra.
/// </summary>
public sealed class VoidCapitalPaymentHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    CapitalPaymentSupport support,
    ILoansUnitOfWork unitOfWork,
    ILoansAuditPublisher auditPublisher,
    LoanDtoComposer composer,
    IValidator<VoidCapitalPaymentCommand> validator)
    : ICommandHandler<VoidCapitalPaymentCommand, LoanDetailDto>
{
    private static readonly string[] VoidedFields = ["reason"];

    public async Task<LoanDetailDto> HandleAsync(VoidCapitalPaymentCommand command, CancellationToken cancellationToken)
    {
        await LoansAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, LoansPermissions.Manage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var calendar = await tenantClock.GetAsync(command.TenantId, cancellationToken);
        var now = calendar.UtcNow;
        var context = await support.LoadAsync(command.TenantId, command.LoanId, calendar.Today, now, cancellationToken);
        if (context.Generated.Count > 0)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        if (context.Payments.All(payment => payment.Id != command.PaymentId))
        {
            throw LoansNotFound.CapitalPayment(command.PaymentId);
        }

        LoanCapitalOperations.Void(context.Loan, context.Payments, command.PaymentId, command.Reason, context.PendingCount, now);

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            LoansAuditActions.CapitalPaymentVoided,
            LoansAuditActions.Resources.CapitalPayment,
            command.PaymentId.ToString(),
            "success",
            VoidedFields,
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.ComposeDetailAsync(context.Loan, calendar.Today, cancellationToken);
    }
}
