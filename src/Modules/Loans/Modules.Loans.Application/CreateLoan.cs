using BuildingBlocks.Application;
using FluentValidation;
using Modules.Loans.Domain;
using Modules.Tenancy.Application;

namespace Modules.Loans.Application;

/// <summary>
/// El cuerpo de <c>POST /loans</c> tal como llega por el cable, con todo anulable para que su ausencia sea un
/// <c>validation.failed</c> con el campo y no un 400 del binding. No lleva <c>type</c>: todo préstamo es <c>InterestOnly</c>.
/// </summary>
public sealed record CreateLoanRequest(
    Guid? CustomerId,
    string? Name,
    decimal? PrincipalAmount,
    string? Currency,
    decimal? MonthlyRatePercent,
    DateOnly? DisbursedOn,
    string? CutoffRule,
    Guid? AssigneeMemberId,
    string? Note);

/// <summary><c>Currency</c> null o en blanco es la moneda del tenant.</summary>
public sealed record CreateLoanCommand(
    Guid TenantId,
    Guid CustomerId,
    string Name,
    decimal PrincipalAmount,
    string? Currency,
    decimal MonthlyRatePercent,
    DateOnly DisbursedOn,
    string CutoffRule,
    Guid? AssigneeMemberId,
    string? Note)
    : ICommand<LoanDetailDto>
{
    public static CreateLoanCommand From(Guid tenantId, CreateLoanRequest request) => new(
        tenantId,
        request.CustomerId ?? Guid.Empty,
        request.Name ?? string.Empty,
        request.PrincipalAmount ?? 0m,
        request.Currency,
        request.MonthlyRatePercent ?? 0m,
        request.DisbursedOn ?? default,
        request.CutoffRule ?? string.Empty,
        request.AssigneeMemberId,
        request.Note);
}

public sealed class CreateLoanValidator : AbstractValidator<CreateLoanCommand>
{
    public CreateLoanValidator()
    {
        RuleFor(command => command.CustomerId).NotEqual(Guid.Empty);
        RuleFor(command => command.Name).MustBeALoanName();
        RuleFor(command => command.PrincipalAmount).MustBeAMoneyAmount();
        RuleFor(command => command.MonthlyRatePercent).MustBeAMonthlyRate();
        RuleFor(command => command.DisbursedOn).MustBeAReasonableDisbursement();
        RuleFor(command => command.CutoffRule).MustBeACutoffRule();
        RuleFor(command => command.Note).MustBeALoanNote();
    }
}

/// <summary>
/// Crea el préstamo y las cuotas de los períodos que el desembolso ya dejó cerrados, todo en una sola transacción con su
/// auditoría. El orden de las validaciones es el del contrato: cliente → encargado → moneda → dominio.
/// </summary>
public sealed class CreateLoanHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    ITenantDefaultCurrency tenantDefaultCurrency,
    ILoansCustomerLookup customers,
    ILoansMemberDirectory members,
    ILoanRepository loans,
    ILoanInstallmentRepository installments,
    ILoansUnitOfWork unitOfWork,
    ILoansAuditPublisher auditPublisher,
    LoanDtoComposer composer,
    IValidator<CreateLoanCommand> validator)
    : ICommandHandler<CreateLoanCommand, LoanDetailDto>
{
    private static readonly string[] CreatedFields =
        ["customerId", "name", "principalAmount", "currency", "monthlyRatePercent", "disbursedOn", "cutoffRule", "assigneeMemberId", "note"];

    public async Task<LoanDetailDto> HandleAsync(CreateLoanCommand command, CancellationToken cancellationToken)
    {
        await LoansAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, LoansPermissions.Manage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        if (!LoanRules.TryParseCutoffRule(command.CutoffRule, out var cutoffRule))
        {
            throw new InvalidOperationException($"CutoffRule '{command.CutoffRule}' passed validation but is not a cutoff rule.");
        }

        // La búsqueda va acotada al tenant de la ruta: un cliente de otro tenant llega como «no existe».
        await LoanRules.EnsureCustomerAsync(customers, command.TenantId, command.CustomerId, cancellationToken);
        await LoanRules.EnsureAssigneeAsync(members, command.TenantId, command.AssigneeMemberId, cancellationToken);
        var currency = await LoanRules.ResolveCurrencyAsync(command.Currency, command.TenantId, tenantDefaultCurrency, cancellationToken);

        var calendar = await tenantClock.GetAsync(command.TenantId, cancellationToken);
        var now = calendar.UtcNow;
        var terms = new LoanTerms(command.CustomerId, command.PrincipalAmount, currency, command.MonthlyRatePercent, command.DisbursedOn, cutoffRule);
        var loan = Loan.Create(LoanId.New(), command.TenantId, terms, command.Name, command.AssigneeMemberId, command.Note, calendar.Today, now);
        loans.Add(loan);
        // Un desembolso en el pasado deja períodos cerrados: se vuelven cuota ya, sin esperar al worker. Sin abonos todavía.
        installments.AddRange(LoanInstallmentGenerator.GenerateClosedPeriods(loan, [], calendar.Today, now));

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            LoansAuditActions.LoanCreated,
            LoansAuditActions.Resources.Loan,
            loan.Id.ToString(),
            "success",
            CreatedFields,
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.ComposeDetailAsync(loan, calendar.Today, cancellationToken);
    }
}
