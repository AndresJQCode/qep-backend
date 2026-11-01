namespace Modules.Loans.Domain;

public sealed record LoanAccrualResult(LoanPeriod Period, IReadOnlyList<InterestSegment> Segments, decimal Amount);

public static class LoanAccrual
{
    /// <summary>
    /// El interés causado del período en curso hasta <paramref name="today"/> inclusive (regla 9 del spec).
    /// Es sólo informativo: no se guarda ni es una cuota. Null si no hay capital que cause interés.
    /// </summary>
    public static LoanAccrualResult? ToDate(Loan loan, IEnumerable<LoanCapitalPayment> payments, DateOnly today)
    {
        if (loan.Status == LoanStatus.Settled || loan.OutstandingPrincipal == 0m)
        {
            return null;
        }

        var period = LoanCalendar.PeriodContaining(loan.CutoffRule, loan.DisbursedOn, today);
        var from = period.Start > loan.DisbursedOn ? period.Start : loan.DisbursedOn;
        var list = payments.ToList();
        var segments = LoanInterestCalculator.Segments(
            period,
            from,
            today,
            LoanCapitalLedger.OpeningBalance(loan, list, from),
            LoanCapitalLedger.ChangesFrom(list, from),
            loan.MonthlyRatePercent);
        return new LoanAccrualResult(period, segments, LoanInterestCalculator.Total(segments));
    }
}
