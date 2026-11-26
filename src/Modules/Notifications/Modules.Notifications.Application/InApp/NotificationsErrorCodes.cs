namespace Modules.Notifications.Application;

public static class NotificationsErrorCodes
{
    /// <summary>The id is not one of the caller's entries: of another member of the same tenant, or nonexistent. Both answer alike, so
    /// the response confirms nothing the caller could not already see.</summary>
    public const string NotificationNotFound = "notifications.notification.not_found";
}
