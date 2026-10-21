namespace Modules.Messaging.Domain;

/// <summary>Franja de atención en minutos locales desde las 00:00: <c>[From, To)</c>, con <c>0 ≤ From &lt; To ≤ 1440</c>. <c>To = 1440</c> es hasta medianoche.</summary>
public readonly record struct TimeRange(int From, int To);

/// <summary>Franjas de un día de la semana: <c>Day</c> 1 = lunes … 7 = domingo. Sin franjas, ese día no se atiende.</summary>
public sealed record WeekdayHours(int Day, IReadOnlyList<TimeRange> Ranges);

/// <summary>Excepción de una fecha local: sus franjas reemplazan al festivo y al día de la semana. Sin franjas, no se atiende.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "«Excepción» es el término del producto y del spec (excepciones de fecha); no es una System.Exception.")]
public sealed record DateException(DateOnly Date, IReadOnlyList<TimeRange> Ranges);

/// <summary>País cuyos festivos cierran el día (D-H9). Hoy sólo Colombia.</summary>
public enum HolidayCountry
{
    CO,
}
