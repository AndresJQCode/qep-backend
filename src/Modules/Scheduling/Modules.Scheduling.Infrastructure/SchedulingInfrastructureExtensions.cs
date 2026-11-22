using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Scheduling.Application;
using Modules.Scheduling.Infrastructure.Persistence;

namespace Modules.Scheduling.Infrastructure;

public static class SchedulingInfrastructureExtensions
{
    public static IServiceCollection AddSchedulingInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'QepDatabase' is required.");

        services.AddDbContext<SchedulingDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    SchedulingDbContext.Schema)));

        services.AddScoped<ISchedulingUnitOfWork, SchedulingUnitOfWork>();
        services.AddScoped<ISchedulingAuditPublisher, SchedulingAuditPublisher>();
        services.AddScoped<ISchedulingProvisioner, SchedulingProvisioner>();
        services.AddScoped<ILocationRepository, LocationRepository>();
        services.AddScoped<IResourceRepository, ResourceRepository>();
        services.AddScoped<IServiceRepository, ServiceRepository>();
        services.AddScoped<IAvailabilityBlockRepository, AvailabilityBlockRepository>();
        services.AddScoped<IClosureRepository, ClosureRepository>();
        services.AddScoped<ISessionRepository, SessionRepository>();
        services.AddScoped<ISchedulingSettingsRepository, SchedulingSettingsRepository>();
        services.AddScoped<ISchedulingSetupReader, SchedulingSetupReader>();

        return services;
    }
}
