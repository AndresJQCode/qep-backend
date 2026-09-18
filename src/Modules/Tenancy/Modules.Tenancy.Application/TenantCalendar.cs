namespace Modules.Tenancy.Application;

/// <summary>
/// Corta instantes en días del tenant (spec 2026-09-17). Los instantes se siguen guardando en UTC;
/// lo que cambia es cómo se cortan en días y cómo se muestran. Puro e inmutable: se construye con el
/// instante actual y el huso, y se prueba sin base ni DI. Lo arma <see cref="ITenantClock"/>.
/// </summary>
public sealed class TenantCalendar
{
    public TenantCalendar(DateTimeOffset utcNow, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);

        UtcNow = utcNow.ToUniversalTime();
        TimeZone = timeZone;
        Today = DateOnly.FromDateTime(ToLocal(UtcNow).DateTime);
    }

    public DateTimeOffset UtcNow { get; }

    public TimeZoneInfo TimeZone { get; }

    /// <summary>La fecha local de <see cref="UtcNow"/>: el "hoy" del negocio.</summary>
    public DateOnly Today { get; }

    /// <summary>El mismo instante en la hora del tenant, para mostrarlo.</summary>
    public DateTimeOffset ToLocal(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, TimeZone);

    /// <summary>
    /// Las 00:00 locales de <paramref name="date"/> como instante. Si caen en el hueco del horario de
    /// verano, el día empieza en el primer instante válido; si ocurren dos veces, en la primera. El
    /// tipo no supone que el huso no tiene horario de verano, aunque Colombia no lo tenga.
    /// </summary>
    public DateTimeOffset StartOfDayUtc(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);

        // Los huecos son de minutos enteros: avanzar de a uno llega al primer instante válido en, a lo
        // sumo, la duración del hueco.
        while (TimeZone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }

        if (TimeZone.IsAmbiguousTime(local))
        {
            // El offset mayor es el de la primera ocurrencia: UTC = local - offset.
            var firstOffset = TimeZone.GetAmbiguousTimeOffsets(local).Max();
            return new DateTimeOffset(local, firstOffset).ToUniversalTime();
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, TimeZone), TimeSpan.Zero);
    }

    /// <summary>Las 00:00 locales del día siguiente: el límite superior exclusivo de
    /// <paramref name="date"/>. "Hasta el 31" incluye todo el 31 del tenant.</summary>
    public DateTimeOffset EndOfDayExclusiveUtc(DateOnly date) => StartOfDayUtc(date.AddDays(1));
}
