namespace Modules.Messaging.Domain;

/// <summary>
/// Spec 2026-10-28: los segundos de atención entre dos instantes bajo un horario, en la zona del tenant. Pura y sin E/S. Sin horario
/// (<c>null</c>) cuenta de corrido. Recorre los días locales y suma el solape de cada franja efectiva con <c>[from, to]</c>: no barre minutos.
/// </summary>
public static class BusinessHoursClock
{
    public static long BusinessSeconds(BusinessHoursSchedule? schedule, DateTimeOffset from, DateTimeOffset to, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        if (to <= from)
        {
            return 0;
        }

        if (schedule is null)
        {
            return (long)(to - from).TotalSeconds;
        }

        var firstDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from, timeZone).DateTime);
        var lastDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(to, timeZone).DateTime);
        long total = 0;
        for (var date = firstDay; date <= lastDay; date = date.AddDays(1))
        {
            foreach (var range in schedule.RangesFor(date))
            {
                var start = BusinessHoursSchedule.LocalToUtc(date, range.From, timeZone);
                var end = BusinessHoursSchedule.LocalToUtc(date, range.To, timeZone);
                var overlapStart = start > from ? start : from;
                var overlapEnd = end < to ? end : to;
                if (overlapEnd > overlapStart)
                {
                    total += (long)(overlapEnd - overlapStart).TotalSeconds;
                }
            }
        }

        return total;
    }
}
