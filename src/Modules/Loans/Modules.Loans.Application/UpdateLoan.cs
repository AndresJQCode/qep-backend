using BuildingBlocks.Application;
using FluentValidation;
using Modules.Loans.Domain;
using Modules.Tenancy.Application;

namespace Modules.Loans.Application;

/// <summary>
/// El cuerpo de <c>PUT /loans/{id}</c>: los mismos campos que <see cref="CreateLoanRequest"/>, todos. Anulable por el mismo motivo.
/// <c>Note</c> y <c>AssigneeMemberId</c> son reemplazo completo: ausente y null limpian, porque el record no distingue una clave
/// ausente de un null.
/// </summary>
public sealed record UpdateLoanRequest(
    Guid? CustomerId,
    string? Name,
    decimal? PrincipalAmount,
    string? Currency,
    decimal? MonthlyRatePercent,
    DateOnly? DisbursedOn,
    string? CutoffRule,
    Guid? AssigneeMemberId,
    string? Note);

/// <param name="ExpectedVersion">La versión que la pantalla cargó; llega por <c>If-Match</c>.</param>
public sealed record UpdateLoanCommand(
    Guid TenantId,
    Guid LoanId,
    Guid CustomerId,
    string Name,
    decimal PrincipalAmount,
    string? Currency,
    decimal MonthlyRatePercent,
    DateOnly DisbursedOn,
    string CutoffRule,
    Guid? AssigneeMemberId,
    string? Note,
    long ExpectedVersion)
    : ICommand<LoanDetailDto>
{
    public static UpdateLoanCommand From(Guid tenantId, Guid loanId, UpdateLoanRequest request, long expectedVersion) => new(
        tenantId,
        loanId,
        request.CustomerId ?? Guid.Empty,
        request.Name ?? string.Empty,
        request.PrincipalAmount ?? 0m,
        request.Currency,
        request.MonthlyRatePercent ?? 0m,
        request.DisbursedOn ?? default,
        request.CutoffRule ?? string.Empty,
        request.AssigneeMemberId,
        request.Note,
        expectedVersion);
}

public sealed class UpdateLoanValidator : AbstractValidator<UpdateLoanCommand>
{
    public UpdateLoanValidator()
    {
        RuleFor(command => command.CustomerId).NotEqual(Guid.Empty);
        RuleFor(command => command.Name).MustBeALoanName();
        RuleFor(command => command.PrincipalAmount).MustBeAMoneyAmount();
        RuleFor(command => command.MonthlyRatePercent).MustBeAMonthlyRate();
        RuleFor(command => command.DisbursedOn).MustBeAReasonableDisbursement();
        RuleFor(command => command.CutoffRule).MustBeACutoffRule();
        RuleFor(command => command.Note).MustBeALoanNote();
        RuleFor(command => command.ExpectedVersion).GreaterThan(0);
    }
}

/// <summary>
/// Sin movimiento (ni cuotas ni filas de abono) cambia todo: condiciones y datos. Con movimiento sólo cambian nombre, encargado y
/// nota, y las condiciones deben llegar iguales: <c>loans.loan.terms_locked</c>. Se compara por valor, así que 100 y 100.00 son
/// el mismo capital. El chequeo va <b>antes</b> de <see cref="Loan.ChangeTerms"/>, que devuelve el capital pendiente al prestado y
/// borraría lo abonado.
/// </summary>
public sealed class UpdateLoanHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    ILoansCustomerLookup customers,
    ILoansMemberDirectory members,
    ILoanRepository loans,
    ILoanInstallmentRepository installments,
    ILoanCapitalPaymentRepository capitalPayments,
    ILoansUnitOfWork unitOfWork,
    ILoansAuditPublisher auditPublisher,
    LoanDtoComposer composer,
    IValidator<UpdateLoanCommand> validator)
    : ICommandHandler<UpdateLoanCommand, LoanDetailDto>
{
    public async Task<LoanDetailDto> HandleAsync(UpdateLoanCommand command, CancellationToken cancellationToken)
    {
        await LoansAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, LoansPermissions.Manage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        if (!LoanRules.TryParseCutoffRule(command.CutoffRule, out var cutoffRule))
        {
            throw new InvalidOperationException($"CutoffRule '{command.CutoffRule}' passed validation but is not a cutoff rule.");
        }

        var loan = await loans.FindAsync(command.TenantId, new LoanId(command.LoanId), cancellationToken)
            ?? throw LoansNotFound.Loan(command.LoanId);
        if (loan.Version != command.ExpectedVersion)
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The loan changed after it was loaded.");
        }

        // En PUT, currency null conserva la moneda del préstamo (no la del tenant, que es el default del alta): si no, un préstamo en
        // otra moneda se bloquearía con terms_locked o la perdería al editar.
        var currency = string.IsNullOrWhiteSpace(command.Currency) ? loan.Currency : LoanRules.NormalizeCurrency(command.Currency);
        var terms = new LoanTerms(command.CustomerId, command.PrincipalAmount, currency, command.MonthlyRatePercent, command.DisbursedOn, cutoffRule);

        var existingInstallments = await installments.ListByLoanAsync(command.TenantId, loan.Id, cancellationToken);
        var existingPayments = await capitalPayments.ListByLoansAsync(command.TenantId, [loan.Id], cancellationToken);
        var hasMovement = LoanRules.HasMovement(existingInstallments, existingPayments);
        if (hasMovement && loan.Terms != terms)
        {
            throw new LoansDomainException(
                LoansErrorCodes.LoanTermsLocked, "A loan with installments or capital payments only accepts changes to name, assignee and note.");
        }

        // Sólo lo que cambia se valida: un encargado que perdió el permiso o un cliente que se desactivó después del alta no
        // bloquean corregir la nota.
        if (command.AssigneeMemberId != loan.AssigneeMemberId)
        {
            await LoanRules.EnsureAssigneeAsync(members, command.TenantId, command.AssigneeMemberId, cancellationToken);
        }

        if (!hasMovement && command.CustomerId != loan.CustomerId)
        {
            await LoanRules.EnsureCustomerAsync(customers, command.TenantId, command.CustomerId, cancellationToken);
        }

        var changedFields = ChangedFields(loan, command, terms);
        var calendar = await tenantClock.GetAsync(command.TenantId, cancellationToken);
        var now = calendar.UtcNow;
        if (!hasMovement)
        {
            loan.ChangeTerms(terms, calendar.Today, now);
            // Mover el desembolso al pasado deja períodos cerrados: se vuelven cuota en esta misma transacción.
            installments.AddRange(LoanInstallmentGenerator.GenerateClosedPeriods(loan, [], calendar.Today, now));
        }

        loan.UpdateDetails(command.Name, command.AssigneeMemberId, command.Note, now);

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            LoansAuditActions.LoanUpdated,
            LoansAuditActions.Resources.Loan,
            loan.Id.ToString(),
            "success",
            changedFields,
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.ComposeDetailAsync(loan, calendar.Today, cancellationToken);
    }

    // Antes de mutar, con los valores como los guarda el dominio (nombre y nota recortados), para que «sin cambios» no cuente.
    private static List<string> ChangedFields(Loan loan, UpdateLoanCommand command, LoanTerms terms)
    {
        var changed = new List<string>(9);
        if (terms.CustomerId != loan.CustomerId)
        {
            changed.Add("customerId");
        }

        if (!string.Equals(loan.Name, command.Name.Trim(), StringComparison.Ordinal))
        {
            changed.Add("name");
        }

        if (terms.PrincipalAmount != loan.PrincipalAmount)
        {
            changed.Add("principalAmount");
        }

        if (!string.Equals(terms.Currency, loan.Currency, StringComparison.Ordinal))
        {
            changed.Add("currency");
        }

        if (terms.MonthlyRatePercent != loan.MonthlyRatePercent)
        {
            changed.Add("monthlyRatePercent");
        }

        if (terms.DisbursedOn != loan.DisbursedOn)
        {
            changed.Add("disbursedOn");
        }

        if (terms.CutoffRule != loan.CutoffRule)
        {
            changed.Add("cutoffRule");
        }

        if (command.AssigneeMemberId != loan.AssigneeMemberId)
        {
            changed.Add("assigneeMemberId");
        }

        var note = string.IsNullOrWhiteSpace(command.Note) ? null : command.Note.Trim();
        if (!string.Equals(loan.Note, note, StringComparison.Ordinal))
        {
            changed.Add("note");
        }

        return changed;
    }
}
