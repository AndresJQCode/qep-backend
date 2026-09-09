using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Platform.Application;
using Modules.Platform.Infrastructure.Persistence;

namespace Modules.Platform.Infrastructure;

public static class PlatformInfrastructureExtensions
{
    public static IServiceCollection AddPlatformInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'QepDatabase' is required.");

        services.AddDbContext<PlatformDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    "platform")));

        services.AddScoped<IRequestFailureRepository, RequestFailureRepository>();
        // Escribe fuera de la transaccion del request, con un DbContext propio que arma con los
        // DbContextOptions que registro AddDbContext. Ver IRequestFailureLog.
        services.AddScoped<IRequestFailureLog, RequestFailureLog>();

        return services;
    }
}
