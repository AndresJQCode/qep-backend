namespace Modules.Loans.Domain;

public static class LoanCalendar
{
    /// <summary>
    /// Período que contiene <paramref name="date"/> según la regla de corte (regla 6 del spec).
    /// Con <see cref="LoanCutoffRule.DisbursementDay"/> el inicio de cada mes sale del día del desembolso
    /// original (el ancla), ajustado al último día del mes si no existe; no del inicio del período anterior,
    /// así un préstamo del 31 de enero vuelve al 31 de marzo después del 28 de febrero.
    /// </summary>
    public static LoanPeriod PeriodContaining(LoanCutoffRule rule, DateOnly disbursedOn, DateOnly date)
    {
        if (rule == LoanCutoffRule.EndOfMonth)
        {
            var first = new DateOnly(date.Year, date.Month, 1);
            return new LoanPeriod(first, first.AddMonths(1).AddDays(-1));
        }

        var anchor = disbursedOn.Day;
        var start = StartIn(anchor, date.Year, date.Month);
        if (date < start)
        {
            var previous = new DateOnly(date.Year, date.Month, 1).AddMonths(-1);
            start = StartIn(anchor, previous.Year, previous.Month);
        }

        var next = new DateOnly(start.Year, start.Month, 1).AddMonths(1);
        return new LoanPeriod(start, StartIn(anchor, next.Year, next.Month).AddDays(-1));
    }

    private static DateOnly StartIn(int anchor, int year, int month) =>
        new(year, month, Math.Min(anchor, DateTime.DaysInMonth(year, month)));
}
