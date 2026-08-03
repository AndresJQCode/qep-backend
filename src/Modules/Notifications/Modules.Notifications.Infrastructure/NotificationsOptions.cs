using Modules.Notifications.Infrastructure.Channels;

namespace Modules.Notifications.Infrastructure;

// Strongly-typed binding of the "Notifications" appsettings section, replacing
// ad-hoc configuration["Notifications:..."] string lookups. Consumers inject
// IOptions<NotificationsOptions>; validation runs at startup
// (see NotificationsOptionsValidator).
public sealed class NotificationsOptions
{
    public const string SectionName = "Notifications";

    public const string LogProvider = "log";

    public const string InfobipProvider = "infobip";

    // "log" (default development channel) or "infobip" (ADR 0018).
    public string EmailProvider { get; init; } = LogProvider;

    public string LoginUrl { get; init; } = "http://localhost:3002/login";

    public InfobipOptions Infobip { get; init; } = new();
}
