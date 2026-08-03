using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Notifications.Infrastructure.Persistence;

namespace Modules.Notifications.Infrastructure;

public static class NotificationsDatabaseInitializer
{
    public static async Task InitializeNotificationsDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
