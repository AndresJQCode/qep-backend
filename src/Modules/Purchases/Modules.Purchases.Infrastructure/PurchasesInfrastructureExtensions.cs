using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Purchases.Application;
using Modules.Purchases.Infrastructure.Persistence;

namespace Modules.Purchases.Infrastructure;

public static class PurchasesInfrastructureExtensions
{
    public static IServiceCollection AddPurchasesInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'QepDatabase' is required.");

        services.AddDbContext<PurchasesDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    PurchasesDbContext.Schema)));

        services.AddScoped<IPurchaseOrderRepository, PurchaseOrderRepository>();
        services.AddScoped<IPurchaseOrderNumberGenerator, PurchaseOrderNumberGenerator>();
        services.AddScoped<IPurchasesUnitOfWork, PurchasesUnitOfWork>();
        services.AddScoped<IPurchasesAuditPublisher, PurchasesAuditPublisher>();

        return services;
    }
}
