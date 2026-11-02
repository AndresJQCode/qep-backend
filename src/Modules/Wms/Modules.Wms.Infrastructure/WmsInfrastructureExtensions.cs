using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Wms.Infrastructure.Persistence;

namespace Modules.Wms.Infrastructure;

public static class WmsInfrastructureExtensions
{
    public static IServiceCollection AddWmsInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException("Connection string 'QepDatabase' is required.");

        services.AddDbContext<WmsDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", WmsDbContext.Schema)));

        return services;
    }
}
