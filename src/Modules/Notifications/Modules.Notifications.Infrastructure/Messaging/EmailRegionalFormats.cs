using Modules.Notifications.Application;
using Modules.Tenancy.Application;

namespace Modules.Notifications.Infrastructure.Messaging;

/// <summary>
/// Arma el <see cref="EmailRegionalFormat"/> de un tenant desde su configuración regional. Vive en
/// Infrastructure porque es la única capa de Notifications que ve Tenancy.
/// </summary>
internal static class EmailRegionalFormats
{
    /// <summary>Sin fila de configuración, el formato colombiano; un patrón o separador desconocido
    /// cae en el mismo valor por defecto que usa Tenancy.</summary>
    public static EmailRegionalFormat For(TimeZoneInfo timeZone, TenantRegionalSettings? settings) =>
        settings is null
            ? EmailRegionalFormat.Colombian(timeZone)
            : new EmailRegionalFormat(
                timeZone,
                RegionalDateFormat.Pattern(settings.DateFormat),
                RegionalNumberFormat.ToNumberFormatInfo(settings.NumberFormat));
}
