using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Support.Application;
using Modules.Support.Infrastructure.Persistence;

namespace Modules.Support.Infrastructure;

public static class SupportInfrastructureExtensions
{
    public static IServiceCollection AddSupportInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'QepDatabase' is required.");

        services.AddDbContext<SupportDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    SupportDbContext.Schema)));

        services.AddScoped<ISupportUnitOfWork, SupportUnitOfWork>();
        services.AddScoped<ISupportProvisioner, SupportProvisioner>();
        services.AddScoped<ISupportTicketNumberGenerator, SupportTicketNumberGenerator>();
        services.AddScoped<ISupportSettingsRepository, SupportSettingsRepository>();
        services.AddScoped<ISupportTicketTypeRepository, SupportTicketTypeRepository>();
        services.AddScoped<ISupportTicketRepository, SupportTicketRepository>();
        services.AddScoped<ISupportTicketMessageRepository, SupportTicketMessageRepository>();
        services.AddScoped<ISupportUnclassifiedRepository, SupportUnclassifiedRepository>();
        services.AddScoped<ISupportReplyRepository, SupportReplyRepository>();
        services.AddScoped<ISupportAuditPublisher, SupportAuditPublisher>();

        // Fase 2 (plan E2): el seguimiento del hilo sondea los entrantes de Mensajería. Apagable por configuración para que las pruebas
        // lo llamen a mano sin que el worker se lleve los entrantes por detrás.
        services.AddScoped<SupportThreadFollower>();
        services.AddOptions<SupportWorkerOptions>().Bind(configuration.GetSection(SupportWorkerOptions.SectionName));
        if (configuration.GetValue($"{SupportWorkerOptions.SectionName}:Enabled", true))
        {
            services.AddHostedService<SupportThreadFollowWorker>();
        }

        return services;
    }
}
