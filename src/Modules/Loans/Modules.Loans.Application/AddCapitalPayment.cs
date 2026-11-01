using BuildingBlocks.Application;
using FluentValidation;
using Modules.Loans.Domain;
using Modules.Tenancy.Application;

namespace Modules.Loans.Application;

public sealed record AddCapitalPaymentCommand(Guid TenantId, Guid LoanId, decimal Amount, DateOnly PaidOn, string? Note)
    : ICommand<LoanDetailDto>, ICapitalPaymentInput
{
    public static AddCapitalPaymentCommand From(Guid tenantId, Guid loanId, CapitalPaymentRequest request) =>
        new(tenantId, loanId, request.Amount ?? 0m, request.PaidOn ?? default, request.Note);
}

public sealed class AddCapitalPaymentValidator : AbstractValidator<AddCapitalPaymentCommand>
{
    public AddCapitalPaymentValidator() => CapitalPaymentRules.Apply(this);
}

/// <summary>
/// Abona a capital: autorización → validador → «hoy» del tenant → carga (genera los períodos cerrados) → guarda lo generado →
/// dominio → abono y cuota final → auditoría → guardar → detalle. Lo generado se guarda <b>antes</b> de decidir: esa generación es
/// correcta pase lo que pase con el abono, y sin guardarla el rechazo hablaría de una cuota que la pantalla no ve.
/// </summary>
public sealed class AddCapitalPaymentHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    CapitalPaymentSupport support,
    ILoanInstallmentRepository installments,
    ILoanCapitalPaymentRepository capitalPayments,
    ILoansUnitOfWork unitOfWork,
    ILoansAuditPublisher auditPublisher,
    LoanDtoComposer composer,
    IValidator<AddCapitalPaymentCommand> validator)
    : ICommandHandler<AddCapitalPaymentCommand, LoanDetailDto>
{
    private static readonly string[] CreatedFields = ["amount", "paidOn", "note"];

    public async Task<LoanDetailDto> HandleAsync(AddCapitalPaymentCommand command, CancellationToken cancellationToken)
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

        var outcome = LoanCapitalOperations.Add(
            context.Loan, context.Payments, command.Amount, command.PaidOn, command.Note, context.PendingCount, calendar.Today, now);
        capitalPayments.Add(outcome.Payment);
        if (outcome.FinalInstallment is { } final)
        {
            installments.AddRange([final]);
        }

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            LoansAuditActions.CapitalPaymentCreated,
            LoansAuditActions.Resources.CapitalPayment,
            outcome.Payment.Id.ToString(),
            "success",
            CreatedFields,
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.ComposeDetailAsync(context.Loan, calendar.Today, cancellationToken);
    }
}
