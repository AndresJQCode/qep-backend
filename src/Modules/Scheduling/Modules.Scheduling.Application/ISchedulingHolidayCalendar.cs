namespace Modules.Scheduling.Application;

/// <summary>
/// Los festivos que cierran las franjas con <c>closes_on_holidays</c> (spec 2026-11-21, D-10). Puerto propio: el cálculo vive en
/// Mensajería (<c>ColombianHolidays</c>) y el adaptador, en Bootstrapper, lo usa sólo con el país del tenant en <c>CO</c>.
/// </summary>
public interface ISchedulingHolidayCalendar
{
    /// <summary>Los festivos del tenant entre las dos fechas, inclusive. Vacío para un tenant que no es de Colombia.</summary>
    Task<IReadOnlySet<DateOnly>> HolidaysAsync(Guid tenantId, DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken);
}
