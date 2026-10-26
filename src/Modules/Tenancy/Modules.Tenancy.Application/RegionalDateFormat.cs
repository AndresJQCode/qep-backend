using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

/// <summary>
/// Resuelve el patrón de fecha del tenant para los textos que arma el servidor. Un valor ausente o
/// desconocido cae en día primero, el formato colombiano.
/// </summary>
public static class RegionalDateFormat
{
    public static string Pattern(string? dateFormat) =>
        TenantDateFormats.IsKnown(dateFormat) ? dateFormat! : TenantDateFormats.DayMonthYear;

    public static string WithTime(string? dateFormat) => Pattern(dateFormat) + " HH:mm";
}
