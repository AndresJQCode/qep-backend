using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.ApiAccess.Application;
using Modules.ApiAccess.Infrastructure.Persistence;

namespace Modules.ApiAccess.Infrastructure;

public static class ApiAccessInfrastructureExtensions
{
    public static IServiceCollection AddApiAccessInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'QepDatabase' is required.");

        services.AddDbContext<ApiAccessDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    ApiAccessDbContext.Schema)));

        services.AddScoped<IApiKeyRepository, ApiKeyRepository>();
        services.AddScoped<IApiAccessUnitOfWork, ApiAccessUnitOfWork>();
        services.AddScoped<IApiAccessAuditRecorder, ApiAccessAuditRecorder>();

        return services;
    }
}
