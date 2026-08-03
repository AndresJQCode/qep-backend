namespace Modules.Notifications.Domain;

public readonly record struct NotificationId(Guid Value)
{
    public static NotificationId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
