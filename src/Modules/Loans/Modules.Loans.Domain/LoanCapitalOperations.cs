namespace Modules.Loans.Domain;

/// <summary>El abono creado y, si saldó el préstamo, la última cuota que generó.</summary>
public sealed record CapitalPaymentOutcome(LoanCapitalPayment Payment, LoanInstallment? FinalInstallment);

/// <summary>
/// Abonar y anular un abono sobre el período abierto. Mutan el préstamo y los abonos recibidos, pero no tocan
/// repositorios: el handler persiste, y el preview puede correr el mismo camino sin escribir.
/// </summary>
public static class LoanCapitalOperations
{
    /// <summary>
    /// Desde cuándo se puede abonar: el inicio del período que sigue al último generado, nunca antes del desembolso.
    /// Se calcula con el calendario y no con <see cref="Loan.NextPeriodToGenerate"/>, que es null en un préstamo
    /// saldado y anular un abono lo necesita justo ahí.
    /// </summary>
    public static DateOnly OpenFrom(Loan loan)
    {
        if (loan.LastGeneratedPeriodEnd is not { } last)
        {
            return loan.DisbursedOn;
        }

        var start = LoanCalendar.PeriodContaining(loan.CutoffRule, loan.DisbursedOn, last.AddDays(1)).Start;
        return start > loan.DisbursedOn ? start : loan.DisbursedOn;
    }

    /// <summary>
    /// Validación en este orden: cuotas pendientes, monto, fecha. Después rebalancea y, si el capital llega a
    /// cero, cierra el período en la fecha del abono más reciente. <paramref name="payments"/> no incluye el nuevo.
    /// </summary>
    public static CapitalPaymentOutcome Add(
        Loan loan,
        IReadOnlyList<LoanCapitalPayment> payments,
        decimal amount,
        DateOnly paidOn,
        string? note,
        int pendingCount,
        DateOnly today,
        DateTimeOffset now)
    {
        if (pendingCount > 0)
        {
            throw new LoansDomainException(LoansErrorCodes.CapitalPaymentPendingInstallments, "Pay or remove the pending installments before a capital payment.");
        }

        if (amount <= 0)
        {
            throw new LoansDomainException(LoansErrorCodes.LoanInvalid, "The amount must be greater than zero.");
        }

        if (amount > loan.OutstandingPrincipal)
        {
            throw new LoansDomainException(LoansErrorCodes.CapitalPaymentExceedsBalance, "The amount exceeds the outstanding principal.");
        }

        var openFrom = OpenFrom(loan);
        if (paidOn < openFrom || paidOn > today)
        {
            throw new LoansDomainException(LoansErrorCodes.CapitalPaymentDateOutOfPeriod, "The date must fall inside the open period.");
        }

        var payment = LoanCapitalPayment.Create(loan.TenantId, loan.Id, amount, paidOn, note, now);
        var all = payments.Append(payment).ToList();
        var balance = Rebalance(loan, all);
        loan.SetOutstanding(balance, now);

        var final = balance == 0m ? CloseSettledPeriod(loan, all, now) : null;
        loan.RefreshStatus(final is null ? pendingCount : pendingCount + 1, now);
        return new CapitalPaymentOutcome(payment, final);
    }

    /// <summary>Anula el abono más reciente mientras no se haya generado una cuota después de él.</summary>
    public static void Void(
        Loan loan, IReadOnlyList<LoanCapitalPayment> payments, Guid paymentId, string reason, int pendingCount, DateTimeOffset now)
    {
        var payment = payments.FirstOrDefault(candidate => candidate.Id == paymentId)
            ?? throw new LoansDomainException(LoansErrorCodes.CapitalPaymentNotFound, "The capital payment does not exist.");
        if (payment.VoidedAt is not null)
        {
            throw new LoansDomainException(LoansErrorCodes.CapitalPaymentAlreadyVoided, "The capital payment is already voided.");
        }

        if (!ReferenceEquals(payment, Latest(payments)))
        {
            throw new LoansDomainException(LoansErrorCodes.CapitalPaymentNotLatest, "Only the latest capital payment can be voided.");
        }

        if (!IsAfterLastGenerated(loan, payment))
        {
            throw new LoansDomainException(LoansErrorCodes.CapitalPaymentInstallmentGenerated, "An installment was generated after this payment.");
        }

        payment.MarkVoided(reason, now);
        loan.SetOutstanding(Rebalance(loan, payments), now);
        loan.RefreshStatus(pendingCount, now);
    }

    /// <summary>El abono que hoy se puede anular, o null si el último ya quedó detrás de una cuota generada.</summary>
    public static LoanCapitalPayment? Voidable(Loan loan, IEnumerable<LoanCapitalPayment> payments) =>
        Latest(payments) is { } latest && IsAfterLastGenerated(loan, latest) ? latest : null;

    private static LoanCapitalPayment? Latest(IEnumerable<LoanCapitalPayment> payments) =>
        LoanCapitalLedger.InOrder(payments) is [.., var latest] ? latest : null;

    private static bool IsAfterLastGenerated(Loan loan, LoanCapitalPayment payment) =>
        loan.LastGeneratedPeriodEnd is not { } last || payment.PaidOn > last;

    // Recalcula el saldo de los abonos del período abierto. Los de períodos cerrados no se reescriben: pueden
    // venir de la carga inicial y su cuota ya está generada.
    private static decimal Rebalance(Loan loan, IReadOnlyList<LoanCapitalPayment> payments)
    {
        var openFrom = OpenFrom(loan);
        var balance = LoanCapitalLedger.OpeningBalance(loan, payments, openFrom);
        foreach (var payment in LoanCapitalLedger.InOrder(payments).Where(payment => payment.PaidOn >= openFrom))
        {
            balance -= payment.Amount;
            payment.SetBalanceAfter(balance);
        }

        return balance;
    }

    // Saldar cierra el período en la fecha del abono más reciente y cobra el interés hasta el día anterior.
    // Si cae el primer día del período no hay nada que cobrar y no se genera cuota.
    private static LoanInstallment? CloseSettledPeriod(Loan loan, IReadOnlyList<LoanCapitalPayment> payments, DateTimeOffset now)
    {
        var closesOn = LoanCapitalLedger.InOrder(payments).Max(payment => payment.PaidOn);
        var period = LoanCalendar.PeriodContaining(loan.CutoffRule, loan.DisbursedOn, closesOn);
        var from = period.Start > loan.DisbursedOn ? period.Start : loan.DisbursedOn;
        var segments = LoanInterestCalculator.Segments(
            period,
            from,
            closesOn.AddDays(-1),
            LoanCapitalLedger.OpeningBalance(loan, payments, from),
            LoanCapitalLedger.ChangesFrom(payments, from),
            loan.MonthlyRatePercent);
        if (segments.Count == 0)
        {
            return null;
        }

        var installment = LoanInstallment.Generate(loan, from, closesOn, period.Days, segments, now);
        loan.MarkGenerated(closesOn, now);
        return installment;
    }
}
