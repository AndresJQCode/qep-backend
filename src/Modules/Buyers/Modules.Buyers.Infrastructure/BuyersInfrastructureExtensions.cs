using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Buyers.Application;
using Modules.Buyers.Infrastructure.Persistence;

namespace Modules.Buyers.Infrastructure;

/// <summary>Registro de servicios de Buyers. Las tareas siguientes lo completan.</summary>
public static class BuyersInfrastructureExtensions
{
    public static IServiceCollection AddBuyersInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException("Connection string 'QepDatabase' is required.");

        services.AddDbContext<BuyersDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    BuyersDbContext.Schema)));

        services.AddScoped<IBuyerAccountRepository, BuyerAccountRepository>();
        services.AddScoped<IBuyerProfileRepository, BuyerProfileRepository>();
        services.AddScoped<IBuyerSessionRepository, BuyerSessionRepository>();
        services.AddScoped<IBuyerEmailTokenRepository, BuyerEmailTokenRepository>();
        services.AddScoped<IBuyerConsentRepository, BuyerConsentRepository>();
        services.AddScoped<IBuyerOutbox, BuyerOutboxWriter>();
        services.AddScoped<IBuyersUnitOfWork, BuyersUnitOfWork>();
        services.AddSingleton<IBuyerPasswordHasher, AspNetCoreBuyerPasswordHasher>();

        return services;
    }
}
