using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Store.Application;
using Modules.Store.Infrastructure.Persistence;

namespace Modules.Store.Infrastructure;

public static class StoreInfrastructureExtensions
{
    public static IServiceCollection AddStoreInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'QepDatabase' is required.");

        services.AddDbContext<StoreDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    StoreDbContext.Schema)));

        services.AddScoped<IOnlineStoreRepository, OnlineStoreRepository>();
        services.AddScoped<IStoreDomainRepository, StoreDomainRepository>();
        services.AddScoped<IStoreOrderRequestRepository, StoreOrderRequestRepository>();
        services.AddScoped<IStoreRequestNumberGenerator, StoreRequestNumberGenerator>();
        services.AddScoped<IStoreUnitOfWork, StoreUnitOfWork>();
        services.AddScoped<IStoreAuditPublisher, StoreAuditPublisher>();

        return services;
    }
}
