using System.Globalization;

namespace Modules.ServiceContracts.Domain;

/// <summary>
/// El calendario fijo de avisos de un cobro (spec §3.3, paso 4): <c>before_5 = due − 5</c>, <c>due</c>, <c>after_3 = due + 3</c> y
/// después semanal, <c>after_{3+7k}</c>. Puro: no sabe qué pasos ya se cubrieron.
/// </summary>
public static class ReminderCalendar
{
    public const string Before5 = "before_5";
    public const string Due = "due";
    private const string AfterPrefix = "after_";
    private const int FirstAfterDays = 3;
    private const int WeekDays = 7;

    /// <summary>
    /// El <b>último</b> paso con fecha <c>&lt;= today</c>, o null si todavía no toca ninguno. Ruling F8: el paso de un cobro es éste
    /// aunque ya esté cubierto (entonces no se manda nada): un día perdido cubre sólo el paso más reciente, nunca los anteriores
    /// que quedaron sin fila (Review Focus 3).
    /// </summary>
    public static string? StepDueOn(DateOnly dueDate, DateOnly today)
    {
        var daysFromDue = today.DayNumber - dueDate.DayNumber;
        if (daysFromDue < -ChargeGenerator.WindowDays)
        {
            return null;
        }

        if (daysFromDue < 0)
        {
            return Before5;
        }

        if (daysFromDue < FirstAfterDays)
        {
            return Due;
        }

        var weeks = (daysFromDue - FirstAfterDays) / WeekDays;
        return After(FirstAfterDays + (weeks * WeekDays));
    }

    /// <summary>La fecha de un paso; lanza <see cref="ArgumentException"/> si el nombre no es un paso del calendario.</summary>
    public static DateOnly DateOf(string step, DateOnly dueDate)
    {
        if (step == Before5)
        {
            return dueDate.AddDays(-ChargeGenerator.WindowDays);
        }

        if (step == Due)
        {
            return dueDate;
        }

        if (step.StartsWith(AfterPrefix, StringComparison.Ordinal)
            && int.TryParse(step.AsSpan(AfterPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var days)
            && days >= FirstAfterDays
            && (days - FirstAfterDays) % WeekDays == 0
            && step == After(days))
        {
            return dueDate.AddDays(days);
        }

        throw new ArgumentException($"'{step}' is not a reminder step.", nameof(step));
    }

    private static string After(int days) => AfterPrefix + days.ToString(CultureInfo.InvariantCulture);
}
