using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Geography.Application;
using Modules.Geography.Infrastructure.Persistence;
using Modules.Geography.Infrastructure.Seed;

namespace Modules.Geography.Infrastructure;

public static class GeographyInfrastructureExtensions
{
    public static IServiceCollection AddGeographyInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'QepDatabase' is required.");

        services.AddDbContext<GeographyDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    "geography")));

        services.AddScoped<IDepartmentRepository, DepartmentRepository>();
        services.AddScoped<ICityRepository, CityRepository>();
        services.AddScoped<GeographySeeder>();

        return services;
    }
}
