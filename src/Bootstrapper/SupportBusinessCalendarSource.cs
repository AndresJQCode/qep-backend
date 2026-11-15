using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Support.Application;
using Modules.Support.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper;

/// <summary>
/// El calendario de Soporte es el mismo de la bandeja [owner 2026-11-14]: el del horario predeterminado del tenant. Para una fecha
/// decide, en este orden, la excepción de fecha (con franjas abre, sin franjas cierra), el festivo del país y el día de la semana:
/// es el orden de <see cref="BusinessHoursSchedule.RangesFor"/>. Si ese orden cambia en Mensajería, cambia acá también; lo amarra
/// <c>SupportBusinessCalendarTests.TheCalendarMatchesTheInboxOnEveryDayOfAYear</c>.
///
/// A diferencia de la bandeja, un horario sin país de festivos usa el país del tenant, y un horario sin ningún día con franjas usa
/// lunes a viernes (plan D12): sin días hábiles en la semana, sumar días no terminaría nunca.
/// </summary>
internal sealed class SupportBusinessCalendar(
    IReadOnlySet<DayOfWeek> workingDays, bool colombianHolidays, IReadOnlyDictionary<DateOnly, bool> exceptionIsOpen)
    : ISupportBusinessCalendar
{
    private static readonly DayOfWeek[] MondayToFriday =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

    public static SupportBusinessCalendar From(BusinessHoursSchedule? schedule, string tenantCountry)
    {
        var workingDays = schedule?.Weekly
            .Where(day => day.Ranges.Count > 0)
            .Select(day => day.Day == 7 ? DayOfWeek.Sunday : (DayOfWeek)day.Day)
            .ToHashSet() ?? [];
        if (workingDays.Count == 0)
        {
            workingDays = [.. MondayToFriday];
        }

        var colombianHolidays = schedule?.HolidayCountry is { } holidayCountry
            ? holidayCountry == HolidayCountry.CO
            : string.Equals(tenantCountry?.Trim(), "CO", StringComparison.OrdinalIgnoreCase);
        var exceptions = (schedule?.Exceptions ?? [])
            .ToDictionary(exception => exception.Date, exception => exception.Ranges.Count > 0);

        return new SupportBusinessCalendar(workingDays, colombianHolidays, exceptions);
    }

    public bool IsBusinessDay(DateOnly day)
    {
        if (exceptionIsOpen.TryGetValue(day, out var open))
        {
            return open;
        }

        if (colombianHolidays && ColombianHolidays.For(day.Year).Contains(day))
        {
            return false;
        }

        return workingDays.Contains(day.DayOfWeek);
    }
}

/// <summary>
/// Arma el <see cref="SupportClockContext"/> de un tenant: el horario predeterminado de Mensajería (se lee aunque el módulo
/// <c>messaging</c> esté apagado: es dato del tenant, plan D13), su país y su zona horaria.
/// </summary>
internal sealed class SupportBusinessCalendarSource(
    IBusinessHoursRepository schedules,
    ITenantDirectory tenants,
    ITenantClock clock) : ISupportBusinessCalendarSource
{
    public async Task<SupportClockContext> GetAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        // El reloj primero: un tenant que no existe falla con tenancy.tenant.not_found antes de leer nada más.
        var calendar = await clock.GetAsync(tenantId, cancellationToken);
        var regional = await tenants.GetRegionalSettingsAsync(new TenantId(tenantId), cancellationToken)
            ?? throw new InvalidOperationException($"Tenant '{tenantId}' has no regional settings.");
        var schedule = await schedules.FindDefaultAsync(tenantId, cancellationToken);

        return new SupportClockContext(SupportBusinessCalendar.From(schedule, regional.Country), calendar.TimeZone);
    }
}
