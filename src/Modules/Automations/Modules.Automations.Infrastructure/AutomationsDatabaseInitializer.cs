using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Automations.Infrastructure.Persistence;

namespace Modules.Automations.Infrastructure;

public static class AutomationsDatabaseInitializer
{
    public static async Task InitializeAutomationsDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AutomationsDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
