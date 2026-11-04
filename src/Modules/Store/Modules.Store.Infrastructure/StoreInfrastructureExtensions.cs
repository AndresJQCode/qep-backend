using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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

        // Secretos y topes de la tienda: validados al arrancar (fuera de Development y Testing, los secretos son obligatorios).
        services.AddOptions<StoreOptions>().Bind(configuration.GetSection(StoreOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<StoreOptions>, StoreOptionsValidator>();
        // Los manejadores piden el valor ya enlazado, no IOptions: Application no referencia Microsoft.Extensions.Options.
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<StoreOptions>>().Value);

        return services;
    }
}
