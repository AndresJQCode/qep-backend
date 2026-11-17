namespace Modules.Support.Domain;

/// <summary>Aritmética de días hábiles sobre un <see cref="ISupportBusinessCalendar"/>. Recorre día por día: los términos son de
/// decenas de días y el calendario no tiene forma cerrada (festivos).</summary>
public static class SupportBusinessDays
{
    /// <summary>Diez años: un calendario sin días hábiles en ese tramo está roto, y seguir buscando colgaría el alta (Review Focus
    /// 1). El de lunes a viernes nunca llega acá; la guarda es del dominio, que no sabe qué calendario le pasan.</summary>
    public const int MaxScanDays = 3660;

    /// <summary>El <paramref name="businessDays"/>-ésimo día hábil estrictamente después de <paramref name="from"/>; con 0, el
    /// mismo <paramref name="from"/>. El término cuenta desde el día hábil siguiente a la radicación (spec §7.2).</summary>
    public static DateOnly Add(ISupportBusinessCalendar calendar, DateOnly from, int businessDays)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentOutOfRangeException.ThrowIfNegative(businessDays);

        var date = from;
        var remaining = businessDays;
        var scanned = 0;
        while (remaining > 0)
        {
            date = date.AddDays(1);
            if (++scanned > MaxScanDays)
            {
                throw NoBusinessDays(from);
            }

            if (calendar.IsBusinessDay(date))
            {
                remaining--;
                scanned = 0;
            }
        }

        return date;
    }

    /// <summary>Cuántos días hábiles <c>d</c> cumplen <paramref name="from"/> &lt; d &lt; <paramref name="to"/>. Si
    /// <paramref name="to"/> no es posterior a <paramref name="from"/>, 0.</summary>
    public static int CountStrictlyBetween(ISupportBusinessCalendar calendar, DateOnly from, DateOnly to)
    {
        ArgumentNullException.ThrowIfNull(calendar);

        var count = 0;
        for (var date = from.AddDays(1); date < to; date = date.AddDays(1))
        {
            if (date.DayNumber - from.DayNumber > MaxScanDays)
            {
                throw new InvalidOperationException(
                    $"The support range {from:yyyy-MM-dd}..{to:yyyy-MM-dd} is longer than {MaxScanDays} days.");
            }

            if (calendar.IsBusinessDay(date))
            {
                count++;
            }
        }

        return count;
    }

    private static InvalidOperationException NoBusinessDays(DateOnly from) =>
        new($"The support calendar has no business day within {MaxScanDays} days after {from:yyyy-MM-dd}.");
}
