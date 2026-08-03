using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modules.Identity.Application;
using Modules.Identity.Infrastructure.Messaging;
using Modules.Identity.Infrastructure.Persistence;

namespace Modules.Identity.Infrastructure;

public static class IdentityInfrastructureExtensions
{
    public static IServiceCollection AddIdentityInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'QepDatabase' is required.");

        services.AddDbContext<IdentityDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    "identity")));
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IIdentityUnitOfWork, IdentityUnitOfWork>();
        services.AddScoped<IIdentityProvisioning, IdentityProvisioningService>();
        services.AddScoped<IProviderLinking, ProviderLinkingService>();
        services.AddScoped<IOwnerProvisioning, OwnerProvisioningService>();
        services.AddScoped<IProviderIdentityResolver, ProviderIdentityResolver>();
        services.AddScoped<IUserDirectory, UserDirectory>();

        services.AddScoped<ISessionRepository, SessionRepository>();
        services.AddScoped<IIdentityAuditRecorder, IdentityAuditRecorder>();
        services.AddScoped<ISessionService, SessionService>();

        var section = configuration.GetSection(QepSessionOptions.SectionName);
        services.AddOptions<QepSessionOptions>()
            .Bind(section)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<QepSessionOptions>, SessionOptionsValidator>();
        services.AddHostedService<SessionRevocationWorker>();

        return services;
    }
}
