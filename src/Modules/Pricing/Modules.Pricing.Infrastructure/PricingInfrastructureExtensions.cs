using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Pricing.Application;
using Modules.Pricing.Infrastructure.Persistence;

namespace Modules.Pricing.Infrastructure;

public static class PricingInfrastructureExtensions
{
    public static IServiceCollection AddPricingInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'QepDatabase' is required.");

        services.AddDbContext<PricingDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    "pricing")));

        services.AddScoped<IPriceListRepository, PriceListRepository>();
        services.AddScoped<IPricingUnitOfWork, PricingUnitOfWork>();
        services.AddScoped<IPricingAuditPublisher, PricingAuditPublisher>();
        services.AddScoped<DefaultPriceListsSeeder>();

        return services;
    }
}
