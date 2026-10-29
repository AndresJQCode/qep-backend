using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Billing.Application;
using Modules.Billing.Infrastructure.Persistence;

namespace Modules.Billing.Infrastructure;

public static class BillingInfrastructureExtensions
{
    public static IServiceCollection AddBillingInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'QepDatabase' is required.");

        services.AddDbContext<BillingDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    "billing")));

        services.AddScoped<IBillingServiceRepository, BillingServiceRepository>();
        services.AddScoped<IChargeRepository, ChargeRepository>();
        services.AddScoped<ICustomerBillingProfileRepository, CustomerBillingProfileRepository>();
        services.AddScoped<IBillingSettingsRepository, BillingSettingsRepository>();
        services.AddScoped<IBillingUnitOfWork, BillingUnitOfWork>();
        services.AddScoped<IBillingAuditPublisher, BillingAuditPublisher>();

        return services;
    }
}
