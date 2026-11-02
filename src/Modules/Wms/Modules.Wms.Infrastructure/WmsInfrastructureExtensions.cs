using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Wms.Application;
using Modules.Wms.Infrastructure.Persistence;

namespace Modules.Wms.Infrastructure;

public static class WmsInfrastructureExtensions
{
    public static IServiceCollection AddWmsInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException("Connection string 'QepDatabase' is required.");

        services.AddDbContext<WmsDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", WmsDbContext.Schema)));

        services.AddScoped<WmsAuthorization>();
        services.AddScoped<WmsCaller>();
        services.AddScoped<IWmsUnitOfWork, WmsUnitOfWork>();
        services.AddScoped<IWmsAuditRecorder, WmsAuditRecorder>();
        services.AddScoped<IWmsProvisioner, WmsProvisioner>();
        services.AddScoped<IWarehouseRepository, WarehouseRepository>();
        services.AddScoped<IOwnerRepository, OwnerRepository>();
        services.AddScoped<IUnitOfMeasureRepository, UnitOfMeasureRepository>();
        services.AddScoped<IVariantProfileRepository, VariantProfileRepository>();
        services.AddScoped<IIdentifierRepository, IdentifierRepository>();

        // Sin libro de inventario no hay existencias: WMS-D reemplaza estas tres por consultas al libro.
        services.AddScoped<IVariantStockProbe, NoVariantStockProbe>();
        services.AddScoped<ILocationStockProbe, NoLocationStockProbe>();
        services.AddScoped<IOwnerStockProbe, NoOwnerStockProbe>();

        return services;
    }
}
