namespace Modules.Loans.Domain;

public static class LoanInstallmentGenerator
{
    /// <summary>
    /// Convierte en cuota cada período ya cerrado, uno por uno hasta el último que terminó antes de
    /// <paramref name="today"/> (la cuota se genera el día siguiente al corte). Avanza el préstamo con
    /// <see cref="Loan.MarkGenerated"/>; quien llama persiste las cuotas devueltas.
    /// </summary>
    public static IReadOnlyList<LoanInstallment> GenerateClosedPeriods(
        Loan loan, IReadOnlyList<LoanCapitalPayment> payments, DateOnly today, DateTimeOffset now)
    {
        var installments = new List<LoanInstallment>();
        while (loan.NextPeriodToGenerate() is { } period && period.End < today)
        {
            var from = period.Start > loan.DisbursedOn ? period.Start : loan.DisbursedOn;
            var segments = LoanInterestCalculator.Segments(
                period,
                from,
                period.End,
                LoanCapitalLedger.OpeningBalance(loan, payments, from),
                LoanCapitalLedger.ChangesFrom(payments, from),
                loan.MonthlyRatePercent);
            installments.Add(LoanInstallment.Generate(loan, from, period.End, period.Days, segments, now));
            loan.MarkGenerated(period.End, now);
        }

        return installments;
    }
}
