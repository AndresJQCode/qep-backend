namespace Modules.Tenancy.Domain;

/// <summary>
/// Los patrones de fecha que un tenant puede elegir. Son patrones de .NET, así que sirven tal cual
/// para formatear en el servidor (PDF, correos, Excel) y se guardan como texto en la configuración.
/// </summary>
public static class TenantDateFormats
{
    public const string IsoDate = "yyyy-MM-dd";
    public const string DayMonthYear = "dd/MM/yyyy";
    public const string MonthDayYear = "MM/dd/yyyy";

    public static bool IsKnown(string? value) =>
        value is IsoDate or DayMonthYear or MonthDayYear;
}
