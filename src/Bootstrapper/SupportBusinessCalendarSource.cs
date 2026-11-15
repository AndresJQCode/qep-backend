using Modules.Messaging.Domain;
using Modules.Support.Application;
using Modules.Support.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper;

/// <summary>
/// Los términos de Soporte cuentan de lunes a viernes [owner 2026-11-15, spec §13.4]: en un tenant de Colombia se descuentan
/// además los festivos nacionales (<see cref="ColombianHolidays"/>, que vive en Mensajería y por eso este adaptador está en
/// Bootstrapper); en cualquier otro país, sólo lunes a viernes. No lee el horario de atención del tenant ni sus excepciones de
/// fecha: un horario que abre el sábado o cierra un martes no mueve un término legal. Lo amarra
/// <c>SupportBusinessCalendarTests.TheCalendarIsMondayToFridayLessColombianHolidaysOnEveryDayOfAYear</c>.
/// </summary>
internal sealed class SupportBusinessCalendar(bool colombianHolidays) : ISupportBusinessCalendar
{
    public static SupportBusinessCalendar From(string tenantCountry) =>
        new(string.Equals(tenantCountry?.Trim(), "CO", StringComparison.OrdinalIgnoreCase));

    public bool IsBusinessDay(DateOnly day) =>
        day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)
        && !(colombianHolidays && ColombianHolidays.For(day.Year).Contains(day));
}

/// <summary>
/// Arma el <see cref="SupportClockContext"/> de un tenant: su país (de los ajustes regionales) y la zona horaria que ya leyó quien
/// llama del reloj del tenant (que es también quien falla primero con <c>tenancy.tenant.not_found</c>).
/// </summary>
internal sealed class SupportBusinessCalendarSource(ITenantDirectory tenants) : ISupportBusinessCalendarSource
{
    public async Task<SupportClockContext> GetAsync(Guid tenantId, TimeZoneInfo timeZone, CancellationToken cancellationToken)
    {
        var regional = await tenants.GetRegionalSettingsAsync(new TenantId(tenantId), cancellationToken)
            ?? throw new InvalidOperationException($"Tenant '{tenantId}' has no regional settings.");

        return new SupportClockContext(SupportBusinessCalendar.From(regional.Country), timeZone);
    }
}
