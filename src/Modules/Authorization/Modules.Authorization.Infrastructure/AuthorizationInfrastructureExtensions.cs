using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Authorization.Application;
using Modules.Authorization.Infrastructure.Persistence;

namespace Modules.Authorization.Infrastructure;

public static class AuthorizationInfrastructureExtensions
{
    public static IServiceCollection AddAuthorizationInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'QepDatabase' is required.");

        services.AddDbContext<AuthorizationDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    "authorization")));

        services.AddScoped<IRoleRepository, RoleRepository>();
        services.AddScoped<ICustomRoleReader, CustomRoleReader>();
        services.AddScoped<IAuthorizationUnitOfWork, AuthorizationUnitOfWork>();

        return services;
    }
}
