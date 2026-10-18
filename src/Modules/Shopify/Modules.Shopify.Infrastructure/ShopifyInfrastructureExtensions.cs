using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Shopify.Application;
using Modules.Shopify.Infrastructure.Persistence;

namespace Modules.Shopify.Infrastructure;

public static class ShopifyInfrastructureExtensions
{
    public static IServiceCollection AddShopifyInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // Mismo guard que los demás módulos: sin la cadena el host no arranca, en vez de fallar en el primer request con un error
        // de Npgsql que no explica nada.
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException("Connection string 'QepDatabase' is required.");

        services.AddDbContext<ShopifyDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", ShopifyDbContext.Schema)));

        services.AddScoped<IShopifyUnitOfWork, ShopifyUnitOfWork>();
        // §9: auditoría atómica propia sobre audit.entries (nunca el IAuditRecorder ligado a TenancyDbContext).
        services.AddScoped<IShopifyAuditRecorder, ShopifyAuditRecorder>();
        return services;
    }
}
