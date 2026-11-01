using FluentValidation;
using Modules.Loans.Domain;

namespace Modules.Loans.Application;

/// <summary>El préstamo, sus abonos (con tracking) y lo que hace falta para decidir un abono.</summary>
/// <param name="PendingCount">Las cuotas pendientes de la base más las que <see cref="Generated"/> acaba de agregar.</param>
/// <param name="Generated">Las cuotas de períodos ya cerrados que esta carga convirtió en cuota; todavía sin guardar.</param>
public sealed record CapitalPaymentContext(
    Loan Loan, List<LoanCapitalPayment> Payments, int PendingCount, IReadOnlyList<LoanInstallment> Generated);

/// <summary>
/// Lo común de abonar, previsualizar y anular. Antes de decidir nada genera los períodos ya cerrados: sin eso, el préstamo
/// quedaría con un período abierto que en realidad terminó, y un abono que salda perdería el interés de ese período.
/// </summary>
public sealed class CapitalPaymentSupport(
    ILoanRepository loans,
    ILoanInstallmentRepository installments,
    ILoanCapitalPaymentRepository capitalPayments)
{
    public async Task<CapitalPaymentContext> LoadAsync(
        Guid tenantId, Guid loanId, DateOnly today, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var loan = await loans.FindAsync(tenantId, new LoanId(loanId), cancellationToken)
            ?? throw LoansNotFound.Loan(loanId);
        var payments = await capitalPayments.ListByLoanAsync(tenantId, loan.Id, cancellationToken);
        var pending = await installments.CountPendingAsync(tenantId, loan.Id, cancellationToken);

        var generated = LoanInstallmentGenerator.GenerateClosedPeriods(loan, payments, today, now);
        installments.AddRange(generated);

        return new CapitalPaymentContext(loan, payments, pending + generated.Count, generated);
    }
}

public interface ICapitalPaymentInput
{
    decimal Amount { get; }

    DateOnly PaidOn { get; }

    string? Note { get; }
}

internal static class CapitalPaymentRules
{
    /// <summary>Monto positivo y de hasta 2 decimales, fecha presente y nota (ya recortada) de hasta 1000 caracteres.</summary>
    public static void Apply<T>(AbstractValidator<T> validator)
        where T : ICapitalPaymentInput
    {
        validator.RuleFor(command => command.Amount).MustBeAMoneyAmount();
        validator.RuleFor(command => command.PaidOn).NotEqual(default(DateOnly));
        validator.RuleFor(command => command.Note)
            .Must(note => note is null || note.Trim().Length <= LoanCapitalPayment.NoteMaxLength)
            .WithMessage($"The note cannot exceed {LoanCapitalPayment.NoteMaxLength} characters.");
    }
}
