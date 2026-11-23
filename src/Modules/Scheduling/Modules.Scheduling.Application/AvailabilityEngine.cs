using Modules.Scheduling.Domain;

namespace Modules.Scheduling.Application;

/// <summary>
/// Los espacios libres se calculan, no se guardan (spec 2026-11-21, «Disponibilidad»). Función pura: expande las franjas en la zona horaria
/// del tenant, genera inicios cada <c>duración</c> del servicio alineados al inicio de la ventana (PD-1: el margen sólo ocupa, no cambia la
/// rejilla que ve el cliente), descuenta festivos, cierres y sesiones ocupadas, recorta por anticipación y horizonte, y suma los cupos.
/// Con cupo mayor que 1, <c>FirstBookingOpens</c> ofrece los inicios libres y las sesiones abiertas del mismo servicio e inicio;
/// <c>PublishedSessions</c> ofrece sólo las sesiones que el tenant publicó.
/// </summary>
public static class AvailabilityEngine
{
    public static IReadOnlyList<AvailableSlot> Compute(AvailabilityQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var earliest = query.Now.AddMinutes(query.Rules.MinNoticeMinutes);
        var latest = query.Now.AddDays(query.Rules.HorizonDays);
        var duration = TimeSpan.FromMinutes(query.Service.DurationMinutes);
        var occupied = TimeSpan.FromMinutes(query.Service.OccupiedMinutes);

        // La primera franja que ofrece un recurso a una hora gana: dos franjas no suman el mismo recurso dos veces.
        var offers = new Dictionary<(DateTimeOffset Start, ResourceId Resource), SlotOption>();
        var order = new List<(DateTimeOffset Start, ResourceId Resource)>();

        foreach (var block in query.Blocks.Where(block => !block.Archived && block.ServiceIds.Contains(query.Service.Id)))
        {
            var resources = block.ResourceIds
                .Where(resource => query.OfferedResources.ContainsKey(resource)
                    && (query.OnlyResource is null || query.OnlyResource == resource))
                .ToArray();
            if (resources.Length == 0)
            {
                continue;
            }

            for (var day = query.FirstDay; day <= query.LastDay; day = day.AddDays(1))
            {
                if (block.ClosesOnHolidays && query.Holidays.Contains(day))
                {
                    continue;
                }

                foreach (var range in block.RangesOn(day))
                {
                    var windowStart = ToInstant(day, range.From, query.TimeZone);
                    var windowEnd = ToInstant(day, range.To, query.TimeZone);

                    void Offer(DateTimeOffset start, ResourceId resource, SlotOption option)
                    {
                        if (start < earliest || start > latest || offers.ContainsKey((start, resource)))
                        {
                            return;
                        }

                        offers[(start, resource)] = option;
                        order.Add((start, resource));
                    }

                    if (block.SessionMode == SessionMode.PublishedSessions)
                    {
                        foreach (var session in query.Sessions.Where(session =>
                            session.IsPublished
                            && (query.IgnoreOccupancy || session.IsActive)
                            && session.ServiceId == query.Service.Id
                            && resources.Contains(session.ResourceId)
                            && session.StartsAt >= windowStart
                            && session.EndsAt <= windowEnd
                            && (query.IgnoreOccupancy || session.HasRoom)))
                        {
                            if (!IsClosed(query, session.ResourceId, session.StartsAt, session.OccupiesUntil))
                            {
                                Offer(
                                    session.StartsAt,
                                    session.ResourceId,
                                    new SlotOption(session.ResourceId, block.Id, session.Capacity - session.BookedCount, session.Id, block.AllowClientChoice));
                            }
                        }

                        continue;
                    }

                    for (var start = windowStart; start + duration <= windowEnd; start += duration)
                    {
                        foreach (var resource in resources)
                        {
                            var option = OptionFor(query, block, resource, start, occupied);
                            if (option is not null)
                            {
                                Offer(start, resource, option);
                            }
                        }
                    }
                }
            }
        }

        return order
            .GroupBy(key => key.Start)
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var options = group.Select(key => offers[key]).ToArray();
                return new AvailableSlot(group.Key, group.Key + duration, options.Sum(option => option.SeatsLeft), options);
            })
            .ToArray();
    }

    private static SlotOption? OptionFor(
        AvailabilityQuery query, AvailabilityBlock block, ResourceId resource, DateTimeOffset start, TimeSpan occupied)
    {
        var occupiesUntil = start + occupied;
        if (IsClosed(query, resource, start, occupiesUntil))
        {
            return null;
        }

        // Rules only: the caller asks whether the tenant offers this start at all, whoever holds it.
        if (query.IgnoreOccupancy)
        {
            return new SlotOption(resource, block.Id, block.CapacityPerResource, null, block.AllowClientChoice);
        }

        var active = query.Sessions.Where(session => session.ResourceId == resource && session.IsActive).ToArray();

        // Una sesión del mismo servicio y el mismo inicio es la misma sesión: con cupo se suma, llena no se ofrece.
        var existing = active.FirstOrDefault(session => session.ServiceId == query.Service.Id && session.StartsAt == start);
        if (existing is not null)
        {
            return existing.HasRoom
                ? new SlotOption(resource, block.Id, existing.Capacity - existing.BookedCount, existing.Id, block.AllowClientChoice)
                : null;
        }

        // Cualquier otra sesión que ocupe parte de ese rango (otra hora, otro servicio, o el margen de la anterior) lo bloquea.
        if (active.Any(session => session.StartsAt < occupiesUntil && start < session.OccupiesUntil))
        {
            return null;
        }

        return new SlotOption(resource, block.Id, block.CapacityPerResource, null, block.AllowClientChoice);
    }

    private static bool IsClosed(AvailabilityQuery query, ResourceId resource, DateTimeOffset start, DateTimeOffset end) =>
        query.OfferedResources.TryGetValue(resource, out var location)
        && query.Closures.Any(closure => closure.AppliesTo(resource, location) && closure.Overlaps(start, end));

    /// <summary>Minutos locales de un día del tenant a un instante en UTC (Npgsql sólo escribe <c>timestamptz</c> con offset cero). Un rango
    /// que termina a las 24:00 cae en la medianoche siguiente.</summary>
    internal static DateTimeOffset ToInstant(DateOnly day, int minuteOfDay, TimeZoneInfo timeZone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified).AddMinutes(minuteOfDay);
        return new DateTimeOffset(local, timeZone.GetUtcOffset(local)).ToUniversalTime();
    }
}
