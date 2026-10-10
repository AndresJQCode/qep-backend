using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Infrastructure.Persistence;

namespace Modules.Messaging.Infrastructure;

public static class MessagingDatabaseInitializer
{
    public static async Task InitializeMessagingDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
