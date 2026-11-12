using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Ai.Infrastructure.Persistence;

namespace Modules.Ai.Infrastructure;

public static class AiDatabaseInitializer
{
    public static async Task InitializeAiDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AiDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
