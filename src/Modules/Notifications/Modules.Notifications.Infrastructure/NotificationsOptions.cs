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

    // Base de los deep-links de invitación: el email lleva "{InvitationUrl}/{token}",
    // que el frontend resuelve contra GET /api/v1/invitations/{token}.
    public string InvitationUrl { get; init; } = "http://localhost:3002/invitations";

    public InfobipOptions Infobip { get; init; } = new();
}
