namespace Modules.Payroll.Domain.Engine;

public sealed record WorkedDays(DateOnly CoveredStart, DateOnly CoveredEnd, int Days, bool FinalSettlementPending);

/// <summary>Paso 1 del motor (R-7): los días que el contrato cubre de la quincena.</summary>
public static class WorkedDaysRule
{
    private const int FullHalfDays = 15;

    public static WorkedDays Compute(PeriodInfo period, DateOnly contractStart, DateOnly? endedOn)
    {
        var coveredStart = contractStart > period.Start ? contractStart : period.Start;
        var coveredEnd = endedOn is { } ended && ended < period.End ? ended : period.End;
        var pending = endedOn is { } end && end >= period.Start && end <= period.End;

        if (coveredStart > coveredEnd)
        {
            return new WorkedDays(coveredStart, coveredEnd, 0, pending);
        }

        // A half always pays 15 days, even when the month gives it 13 (February) or 16 (31-day months); a partial one counts real days.
        var days = coveredStart == period.Start && coveredEnd == period.End
            ? FullHalfDays
            : Math.Min(FullHalfDays, coveredEnd.DayNumber - coveredStart.DayNumber + 1);
        return new WorkedDays(coveredStart, coveredEnd, days, pending);
    }
}
