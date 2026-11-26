using Modules.Messaging.Domain;
using Modules.Scheduling.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper;

/// <summary>
/// Los festivos de la Agenda (spec 2026-11-21, D-10). En un tenant de Colombia son los nacionales de <see cref="ColombianHolidays"/>, que
/// vive en Mensajería y por eso este adaptador está en Bootstrapper; en cualquier otro país no se cierra nada. Calco de
/// <c>SupportBusinessCalendar</c>.
/// </summary>
internal static class SchedulingHolidays
{
    public static IReadOnlySet<DateOnly> Between(string? country, DateOnly from, DateOnly to)
    {
        if (!string.Equals(country?.Trim(), "CO", StringComparison.OrdinalIgnoreCase) || from > to)
        {
            return new HashSet<DateOnly>();
        }

        var holidays = new HashSet<DateOnly>();
        for (var year = from.Year; year <= to.Year; year++)
        {
            holidays.UnionWith(ColombianHolidays.For(year).Where(day => day >= from && day <= to));
        }

        return holidays;
    }
}

/// <summary>Lee el país de los ajustes regionales del tenant. Un tenant sin ajustes falla en vez de adivinar el país: sólo pasa con el
/// stub de desarrollo, y una agenda sin sus festivos cerraría mal sin avisar.</summary>
internal sealed class SchedulingHolidayCalendar(ITenantDirectory tenants) : ISchedulingHolidayCalendar
{
    public async Task<IReadOnlySet<DateOnly>> HolidaysAsync(
        Guid tenantId, DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken)
    {
        var regional = await tenants.GetRegionalSettingsAsync(new TenantId(tenantId), cancellationToken)
            ?? throw new InvalidOperationException($"Tenant '{tenantId}' has no regional settings.");

        return SchedulingHolidays.Between(regional.Country, firstDay, lastDay);
    }
}
