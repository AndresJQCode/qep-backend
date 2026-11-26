using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Wms.Infrastructure.Persistence;

namespace Modules.Wms.Infrastructure;

public static class WmsDatabaseInitializer
{
    public static async Task InitializeWmsDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WmsDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
