using Modules.Notifications.Infrastructure.Channels;

namespace Modules.Notifications.Infrastructure;

// Binding fuertemente tipado de la sección "Notifications" de appsettings, en reemplazo
// de las búsquedas ad-hoc configuration["Notifications:..."]. Los consumidores inyectan
// IOptions<NotificationsOptions>; la validación corre al arrancar
// (ver NotificationsOptionsValidator).
public sealed class NotificationsOptions
{
    public const string SectionName = "Notifications";

    public const string LogProvider = "log";

    public const string InfobipProvider = "infobip";

    // "log" (canal de desarrollo por defecto) o "infobip" (ADR 0018).
    public string EmailProvider { get; init; } = LogProvider;

    public string LoginUrl { get; init; } = "http://localhost:3002/login";

    public InfobipOptions Infobip { get; init; } = new();
}
