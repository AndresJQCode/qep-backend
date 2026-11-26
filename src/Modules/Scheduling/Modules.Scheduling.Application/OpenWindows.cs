using Modules.Scheduling.Domain;

namespace Modules.Scheduling.Application;

/// <summary>Un tramo de tiempo en instantes UTC.</summary>
public readonly record struct TimeWindow(DateTimeOffset Start, DateTimeOffset End);

/// <summary>
/// Cuándo abre un recurso en un rango de días, para pintar el fondo de su columna en el calendario. Usa las mismas reglas de expansión que
/// <see cref="AvailabilityEngine"/> (zona horaria del tenant, vigencia, festivos) pero no descuenta bloqueos ni citas: es el horario, no lo
/// libre. En una franja de sesiones publicadas el recurso sólo abre donde hay una sesión publicada y activa dentro de la ventana de la
/// franja, porque ahí es donde el motor ofrece algo. Función pura.
/// </summary>
public static class OpenWindows
{
    public static IReadOnlyList<TimeWindow> For(
        IReadOnlyList<AvailabilityBlock> blocks,
        IReadOnlyList<Session> sessions,
        ResourceId resource,
        DateOnly firstDay,
        DateOnly lastDay,
        IReadOnlySet<DateOnly> holidays,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(holidays);
        ArgumentNullException.ThrowIfNull(timeZone);

        var found = new List<TimeWindow>();
        foreach (var block in blocks.Where(block => !block.Archived && block.ResourceIds.Contains(resource)))
        {
            for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
            {
                if (block.ClosesOnHolidays && holidays.Contains(day))
                {
                    continue;
                }

                foreach (var range in block.RangesOn(day))
                {
                    var start = AvailabilityEngine.ToInstant(day, range.From, timeZone);
                    var end = AvailabilityEngine.ToInstant(day, range.To, timeZone);
                    if (block.SessionMode != SessionMode.PublishedSessions)
                    {
                        found.Add(new TimeWindow(start, end));
                        continue;
                    }

                    found.AddRange(sessions
                        .Where(session => session.IsPublished
                            && session.IsActive
                            && session.ResourceId == resource
                            && block.ServiceIds.Contains(session.ServiceId)
                            && session.StartsAt >= start
                            && session.EndsAt <= end)
                        .Select(session => new TimeWindow(session.StartsAt, session.EndsAt)));
                }
            }
        }

        return Merge(found);
    }

    // Dos tramos que se tocan en un extremo (12:00 y 12:00) son uno: la pantalla no debe pintar una costura donde no hay corte.
    private static List<TimeWindow> Merge(List<TimeWindow> windows)
    {
        var merged = new List<TimeWindow>();
        foreach (var window in windows.OrderBy(window => window.Start).ThenBy(window => window.End))
        {
            if (merged.Count > 0 && window.Start <= merged[^1].End)
            {
                merged[^1] = merged[^1] with { End = window.End > merged[^1].End ? window.End : merged[^1].End };
            }
            else
            {
                merged.Add(window);
            }
        }

        return merged;
    }
}
