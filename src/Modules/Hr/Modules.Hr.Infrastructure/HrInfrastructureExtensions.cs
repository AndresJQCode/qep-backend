using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Hr.Application;
using Modules.Hr.Infrastructure.Persistence;

namespace Modules.Hr.Infrastructure;

public static class HrInfrastructureExtensions
{
    public static IServiceCollection AddHrInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'QepDatabase' is required.");

        services.AddDbContext<HrDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    HrDbContext.Schema)));

        services.AddScoped<IHrUnitOfWork, HrUnitOfWork>();
        services.AddScoped<IHrAuditPublisher, HrAuditPublisher>();

        return services;
    }
}
