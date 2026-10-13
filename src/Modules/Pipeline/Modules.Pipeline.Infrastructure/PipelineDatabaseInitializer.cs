using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Pipeline.Infrastructure.Persistence;

namespace Modules.Pipeline.Infrastructure;

public static class PipelineDatabaseInitializer
{
    public static async Task InitializePipelineDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PipelineDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
