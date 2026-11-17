namespace Modules.Support.Domain;

/// <summary>El calendario y la zona horaria del tenant con que el ticket cuenta su plazo. Los días se cuentan en la fecha local:
/// una radicación de noche en Bogotá ya es otro día en UTC (Review Focus 2).</summary>
public sealed record SupportClockContext(ISupportBusinessCalendar Calendar, TimeZoneInfo TimeZone)
{
    public DateOnly LocalDate(DateTimeOffset instant) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, TimeZone).DateTime);
}
