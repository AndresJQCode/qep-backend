using BuildingBlocks.Application;
using FluentValidation;
using Modules.Loans.Domain;
using Modules.Tenancy.Application;

namespace Modules.Loans.Application;

/// <summary>Cuerpo de <c>POST …/capital-payments</c> y de <c>…/preview</c>; todo anulable para que su ausencia sea <c>validation.failed</c> con el campo.</summary>
public sealed record CapitalPaymentRequest(decimal? Amount, DateOnly? PaidOn, string? Note);

public sealed record PreviewCapitalPaymentCommand(Guid TenantId, Guid LoanId, decimal Amount, DateOnly PaidOn, string? Note)
    : ICommand<LoanCapitalPaymentPreviewDto>, ICapitalPaymentInput
{
    public static PreviewCapitalPaymentCommand From(Guid tenantId, Guid loanId, CapitalPaymentRequest request) =>
        new(tenantId, loanId, request.Amount ?? 0m, request.PaidOn ?? default, request.Note);
}

public sealed class PreviewCapitalPaymentValidator : AbstractValidator<PreviewCapitalPaymentCommand>
{
    public PreviewCapitalPaymentValidator() => CapitalPaymentRules.Apply(this);
}

/// <summary>
/// Dice qué pasaría con un abono sin escribir nada. Comparte con el POST la carga y la operación del dominio, así que el aviso
/// no puede diferir de lo que el POST crea; no llama a <c>SaveChanges</c> y el contexto del request se descarta con los cambios
/// en memoria. Es POST sólo porque lleva cuerpo.
/// </summary>
public sealed class PreviewCapitalPaymentHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    CapitalPaymentSupport support,
    IValidator<PreviewCapitalPaymentCommand> validator)
    : ICommandHandler<PreviewCapitalPaymentCommand, LoanCapitalPaymentPreviewDto>
{
    public async Task<LoanCapitalPaymentPreviewDto> HandleAsync(PreviewCapitalPaymentCommand command, CancellationToken cancellationToken)
    {
        await LoansAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, LoansPermissions.Manage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var calendar = await tenantClock.GetAsync(command.TenantId, cancellationToken);
        var context = await support.LoadAsync(command.TenantId, command.LoanId, calendar.Today, calendar.UtcNow, cancellationToken);
        var outcome = LoanCapitalOperations.Add(
            context.Loan,
            context.Payments,
            command.Amount,
            command.PaidOn,
            command.Note,
            context.PendingCount,
            calendar.Today,
            calendar.UtcNow);

        return new LoanCapitalPaymentPreviewDto(
            context.Loan.OutstandingPrincipal,
            context.Loan.OutstandingPrincipal == 0m,
            outcome.FinalInstallment is { } final ? ToFinalInstallmentDto(final) : null);
    }

    private static LoanFinalInstallmentDto ToFinalInstallmentDto(LoanInstallment installment) => new(
        installment.PeriodStart,
        installment.PeriodEnd,
        installment.PeriodDays ?? 0,
        installment.Amount,
        installment.Currency,
        installment.Segments
            .Select(segment => new LoanSegmentDto(segment.StartOn, segment.EndOn, segment.Principal, segment.Days, segment.Interest))
            .ToList());
}
