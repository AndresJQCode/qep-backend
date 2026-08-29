using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Audit.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Infrastructure.Messaging;
using Modules.Tenancy.Infrastructure.Persistence;

namespace Modules.Tenancy.Infrastructure;

public static class TenancyInfrastructureExtensions
{
    public static IServiceCollection AddTenancyInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'QepDatabase' is required.");

        services.AddDbContext<TenancyDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    "platform")));
        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<ITenantDirectory, TenantDirectory>();
        services.AddScoped<IMembershipRepository, MembershipRepository>();
        services.AddScoped<IMembershipActivation, MembershipActivationService>();
        services.AddScoped<ITenantRegistration, TenantRegistrationService>();
        services.AddScoped<IMembershipDirectory, MembershipDirectory>();
        services.AddScoped<IMembershipRoleUsage, MembershipRoleUsage>();
        services.AddScoped<IActiveTenantsQuery, ActiveTenantsQuery>();
        services.AddScoped<ITenancyUnitOfWork, TenancyUnitOfWork>();
        services.AddScoped<IAuditRecorder, TenancyAuditRecorder>();
        services.AddScoped<IOutboxWriter, OutboxWriter>();

        services.AddScoped<IIntegrationEventHandler, TenantSettingsChangeLogProjection>();
        services.AddScoped<IIntegrationEventDispatcher, IntegrationEventDispatcher>();
        services.AddScoped<IOutboxProcessor, OutboxProcessor>();
        services.AddHostedService<OutboxPublisherWorker>();
        return services;
    }
}
