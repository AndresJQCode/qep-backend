using System.Globalization;

namespace Modules.Notifications.Application;

/// <summary>
/// Cómo se escriben el vencimiento y los conteos en un correo: el calendario del tenant, el patrón de
/// su fecha y sus separadores numéricos (spec 2026-10-23, D-R2). Este módulo no ve Tenancy, así que
/// quien arma el correo entrega ya resueltos los tres valores; <see cref="Colombian"/> es el formato
/// cuando el tenant no tiene configuración.
/// </summary>
public sealed record EmailRegionalFormat(TimeZoneInfo TimeZone, string DateFormat, NumberFormatInfo Numbers)
{
    /// <summary>
    /// Día primero y punto como separador de miles: la copia local del valor por defecto de Tenancy.
    /// La amarra <c>ColombianMatchesTheTenancyFallback</c>, porque esta capa no puede referenciarlo.
    /// </summary>
    public static EmailRegionalFormat Colombian(TimeZoneInfo timeZone)
    {
        var numbers = (NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone();
        numbers.NumberGroupSeparator = ".";
        numbers.NumberDecimalSeparator = ",";
        numbers.NumberGroupSizes = [3];
        return new EmailRegionalFormat(timeZone, "dd/MM/yyyy", NumberFormatInfo.ReadOnly(numbers));
    }

    /// <summary>
    /// El instante en la hora del tenant y sin etiqueta de huso. <c>InvariantCulture</c> y no la del
    /// proceso: con otra cultura (<c>de-DE</c>) la <c>/</c> del patrón se escribiría <c>.</c> y el
    /// correo diría una fecha que el tenant no configuró.
    /// </summary>
    public string Expiry(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, TimeZone)
            .ToString(DateFormat + " HH:mm", CultureInfo.InvariantCulture);

    public string Count(int value) => value.ToString("N0", Numbers);
}
