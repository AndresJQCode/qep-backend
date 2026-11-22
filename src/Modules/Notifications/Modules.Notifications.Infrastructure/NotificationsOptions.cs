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

    // Base de los links que no son de una invitación (verificación de correo, restablecer la
    // contraseña): el email lleva "{AppBaseUrl}/<ruta del frontend>?token=...".
    public string AppBaseUrl { get; init; } = "http://localhost:3002";

    public InfobipOptions Infobip { get; init; } = new();

    // Spec 2026-11-22: la campana de avisos.
    public InAppOptions InApp { get; init; } = new();
}

public sealed class InAppOptions
{
    public const int MinRetentionDays = 1;

    public const int MaxRetentionDays = 365;

    // Días que se conserva un aviso de la campana (spec D-11); pasados, el worker de retención lo borra.
    public int RetentionDays { get; init; } = 90;
}
