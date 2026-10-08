using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
        services.AddScoped<ITenantModules, TenantModules>();
        services.AddScoped<ITenantModuleRepository, TenantModuleRepository>();
        services.AddScoped<ITenantDirectory, TenantDirectory>();
        // Spec 2026-09-17: el día de negocio es el del tenant. Scoped para memorizar el huso por
        // request (TenantClock).
        services.AddScoped<ITenantClock, TenantClock>();
        services.AddScoped<IMembershipRepository, MembershipRepository>();
        services.AddScoped<IMembershipActivation, MembershipActivationService>();
        services.AddScoped<IInvitationService, InvitationService>();
        services.AddScoped<ITenantRegistration, TenantRegistrationService>();
        // Spec 2026-10-07: ValidateOnStart, como Notifications, para que un valor que no es
        // booleano tumbe el arranque en vez del primer signup.
        services.AddOptions<EntitlementsOptions>()
            .Bind(configuration.GetSection(EntitlementsOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<ITenantModuleDefaults, TenantModuleDefaults>();
        // Spec 2026-10-08 §1: opcional, pero nunca Guid.Empty. ValidateOnStart, como Entitlements,
        // para que un valor que no es Guid tumbe el arranque y no el primer request.
        services.AddOptions<OperatorTenantOptions>()
            .Bind(configuration.GetSection(OperatorTenantOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<OperatorTenantOptions>, OperatorTenantOptionsValidator>();
        services.AddSingleton<IOperatorTenant, OperatorTenant>();
        services.AddHostedService<OperatorTenantStartupWarning>();
        services.AddScoped<IMembershipDirectory, MembershipDirectory>();
        services.AddScoped<IMembershipRoleUsage, MembershipRoleUsage>();
        services.AddScoped<IActiveTenantsQuery, ActiveTenantsQuery>();
        services.AddScoped<ITenancyUnitOfWork, TenancyUnitOfWork>();
        services.AddScoped<IAuditRecorder, TenancyAuditRecorder>();
        services.AddScoped<IOutboxWriter, OutboxWriter>();
        // Sonda que Identity consulta antes de borrar un usuario huérfano (OrphanUserCleanupWorker).
        services.AddScoped<IUserReferenceProbe, MembershipUserReferenceProbe>();
        // Y lo que ese worker le pide borrar cuando la sonda no lo retiene (spec 2026-10-02).
        services.AddScoped<IUserReferencePurger, MembershipUserReferencePurger>();

        services.AddScoped<IIntegrationEventHandler, TenantSettingsChangeLogProjection>();
        services.AddScoped<IIntegrationEventDispatcher, IntegrationEventDispatcher>();
        services.AddScoped<IOutboxProcessor, OutboxProcessor>();
        services.AddHostedService<OutboxPublisherWorker>();
        return services;
    }
}
