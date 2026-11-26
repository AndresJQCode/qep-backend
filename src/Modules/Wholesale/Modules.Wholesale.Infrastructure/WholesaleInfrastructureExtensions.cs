using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Wholesale.Application;
using Modules.Wholesale.Infrastructure.Messaging;
using Modules.Wholesale.Infrastructure.Persistence;

namespace Modules.Wholesale.Infrastructure;

/// <summary>Persistencia y outbox del módulo mayorista, y el consumidor del aviso por WhatsApp de la invitación (spec
/// 2026-11-18): su procesador es singleton sin estado propio y su worker reclama en el inbox del módulo.</summary>
public static class WholesaleInfrastructureExtensions
{
    public static IServiceCollection AddWholesaleInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'QepDatabase' is required.");

        services.AddDbContext<WholesaleDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    WholesaleDbContext.Schema)));

        services.AddScoped<IDistributorRepository, DistributorRepository>();
        services.AddScoped<IWholesaleUnitOfWork, WholesaleUnitOfWork>();
        services.AddScoped<IWholesaleAuditPublisher, WholesaleAuditPublisher>();
        services.AddScoped<IWholesaleEventPublisher, WholesaleEventPublisher>();
        services.AddScoped<DistributorInvitationNotices>();
        services.AddSingleton<InvitationNoticeProcessor>();
        services.AddHostedService<InvitationNoticeWorker>();

        return services;
    }
}
