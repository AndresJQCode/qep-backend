using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Pos.Infrastructure.Persistence;

namespace Modules.Pos.Infrastructure;

public static class PosDatabaseInitializer
{
    public static async Task InitializePosDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PosDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
