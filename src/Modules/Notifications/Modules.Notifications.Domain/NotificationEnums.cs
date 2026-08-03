namespace Modules.Notifications.Domain;

public enum NotificationChannel
{
    Email = 1,
    Internal = 2
}

public enum NotificationStatus
{
    Pending = 1,
    Sent = 2,
    Failed = 3,
    Retrying = 4
}
